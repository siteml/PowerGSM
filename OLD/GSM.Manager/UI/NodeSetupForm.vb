Imports System.Drawing
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports GSM.Core
Imports GSM.Data

' ============================================================
'  NodeSetupForm
'
'  Adds a new node to the manager. Collects:
'    - Display name
'    - Hostname / IP
'    - Port
'    - Auth token (encrypted via DPAPI before saving)
'    - OS (Windows / Linux)
'
'  Tests the connection before saving so the operator knows
'  the token and address are correct before committing.
' ============================================================

Public Class NodeSetupForm
    Inherits Form

    Private ReadOnly _credentialService As CredentialService
    Private ReadOnly _dbFactory As IDbContextFactory(Of GsmDbContext)

    ' Controls
    Private _txtDisplayName As TextBox
    Private _txtHostname As TextBox
    Private _numPort As NumericUpDown
    Private _txtToken As TextBox
    Private _cboOs As ComboBox
    Private _btnTest As Button
    Private _lblTestResult As Label
    Private _btnSave As Button
    Private _btnCancel As Button

    Public Property CreatedNodeId As String

    Public Sub New(credentialService As CredentialService,
                   dbFactory As IDbContextFactory(Of GsmDbContext))
        _credentialService = credentialService
        _dbFactory = dbFactory

        Text = "Add Node"
        Size = New Size(460, 380)
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MaximizeBox = False
        MinimizeBox = False

        BuildLayout()
    End Sub

    Private Sub BuildLayout()
        Const LabelWidth = 120
        Const InputLeft = 132
        Const InputWidth = 290
        Dim y = 16

        Controls.Add(MakeLabel("Display name:", y, LabelWidth))
        _txtDisplayName = New TextBox With {
            .Location = New Point(InputLeft, y),
            .Size = New Size(InputWidth, 23),
            .PlaceholderText = "e.g. Game Server 1"
        }
        Controls.Add(_txtDisplayName)
        y += 36

        Controls.Add(MakeLabel("Hostname / IP:", y, LabelWidth))
        _txtHostname = New TextBox With {
            .Location = New Point(InputLeft, y),
            .Size = New Size(InputWidth, 23),
            .PlaceholderText = "e.g. 192.168.1.100 or server.example.com"
        }
        Controls.Add(_txtHostname)
        y += 36

        Controls.Add(MakeLabel("Port:", y, LabelWidth))
        _numPort = New NumericUpDown With {
            .Location = New Point(InputLeft, y),
            .Size = New Size(100, 23),
            .Minimum = 1024,
            .Maximum = 65535,
            .Value = 8765
        }
        Controls.Add(_numPort)
        y += 36

        Controls.Add(MakeLabel("Auth token:", y, LabelWidth))
        _txtToken = New TextBox With {
            .Location = New Point(InputLeft, y),
            .Size = New Size(InputWidth, 23),
            .PasswordChar = "●"c,
            .PlaceholderText = "Paste the token from nodesettings.json"
        }
        Controls.Add(_txtToken)
        y += 36

        Controls.Add(MakeLabel("OS:", y, LabelWidth))
        _cboOs = New ComboBox With {
            .Location = New Point(InputLeft, y),
            .Size = New Size(140, 23),
            .DropDownStyle = ComboBoxStyle.DropDownList
        }
        _cboOs.Items.AddRange({"Windows", "Linux"})
        _cboOs.SelectedIndex = 0
        Controls.Add(_cboOs)
        y += 48

        ' Test connection button
        _btnTest = New Button With {
            .Text = "Test Connection",
            .Location = New Point(InputLeft, y),
            .Size = New Size(130, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler _btnTest.Click, AddressOf OnTestClick
        Controls.Add(_btnTest)

        _lblTestResult = New Label With {
            .Location = New Point(InputLeft + 138, y + 4),
            .Size = New Size(180, 20),
            .ForeColor = Color.Gray,
            .Text = "Not tested yet"
        }
        Controls.Add(_lblTestResult)
        y += 44

        ' Save / Cancel
        _btnSave = New Button With {
            .Text = "Save",
            .Location = New Point(ClientSize.Width - 200, y),
            .Size = New Size(88, 28),
            .FlatStyle = FlatStyle.Flat,
            .BackColor = Color.FromArgb(0, 120, 0),
            .ForeColor = Color.White
        }
        AddHandler _btnSave.Click, AddressOf OnSaveClick
        Controls.Add(_btnSave)

        _btnCancel = New Button With {
            .Text = "Cancel",
            .Location = New Point(ClientSize.Width - 104, y),
            .Size = New Size(88, 28),
            .DialogResult = DialogResult.Cancel
        }
        Controls.Add(_btnCancel)

        AcceptButton = _btnSave
        CancelButton = _btnCancel
    End Sub

    Private Shared Function MakeLabel(text As String, y As Integer,
                                       width As Integer) As Label
        Return New Label With {
            .Text = text,
            .Location = New Point(12, y + 4),
            .Size = New Size(width, 20)
        }
    End Function

    Private Async Sub OnTestClick(sender As Object, e As EventArgs)
        If Not ValidateInputs() Then Return

        _btnTest.Enabled = False
        _lblTestResult.Text = "Testing..."
        _lblTestResult.ForeColor = Color.Gray

        Dim hostname = _txtHostname.Text.Trim()
        Dim port = CInt(_numPort.Value)
        Dim token = _txtToken.Text.Trim()

        Task.Run(Async Function()
                     Try
                         ' Build a temporary HttpClient to test the connection.
                         Using http As New System.Net.Http.HttpClient()
                             http.BaseAddress = New Uri($"http://{hostname}:{port}")
                             http.DefaultRequestHeaders.Authorization =
                                 New System.Net.Http.Headers.AuthenticationHeaderValue(
                                     "Bearer", token)
                             http.Timeout = TimeSpan.FromSeconds(8)

                             Dim response = Await http.GetAsync("/api/version")
                             Dim body = Await response.Content.ReadAsStringAsync()

                             Dim versionResp = System.Text.Json.JsonSerializer.
                                 Deserialize(Of GSM.Node.Api.NodeVersionResponse)(
                                     body,
                                     New System.Text.Json.JsonSerializerOptions With {
                                         .PropertyNameCaseInsensitive = True
                                     })

                             BeginInvoke(Sub()
                                             _lblTestResult.Text =
                                                 $"✓ {versionResp.Os} · API {versionResp.ApiVersion}"
                                             _lblTestResult.ForeColor = Color.DarkGreen

                                             ' Auto-set OS dropdown from node's response.
                                             Dim osIdx = _cboOs.Items.IndexOf(versionResp.Os)
                                             If osIdx >= 0 Then _cboOs.SelectedIndex = osIdx

                                             _btnTest.Enabled = True
                                         End Sub)
                         End Using

                     Catch ex As Exception
                         BeginInvoke(Sub()
                                         _lblTestResult.Text = "✗ " & ex.Message
                                         _lblTestResult.ForeColor = Color.Red
                                         _btnTest.Enabled = True
                                     End Sub)
                     End Try
                 End Function)
    End Sub

    Private Async Sub OnSaveClick(sender As Object, e As EventArgs)
        If Not ValidateInputs() Then Return

        _btnSave.Enabled = False

        Dim displayName = _txtDisplayName.Text.Trim()
        Dim hostname = _txtHostname.Text.Trim()
        Dim port = CInt(_numPort.Value)
        Dim token = _txtToken.Text.Trim()
        Dim os = If(_cboOs.SelectedItem?.ToString(), "Windows")

        ' Encrypt the token via DPAPI before persisting.
        Dim encryptedToken = _credentialService.EncryptNodeToken(token)

        Task.Run(Async Function()
                     Try
                         Using db = _dbFactory.CreateDbContext()
                             Dim nodeId = Guid.NewGuid().ToString()
                             Dim entity As New NodeEntity With {
                                 .NodeId = nodeId,
                                 .DisplayName = displayName,
                                 .Hostname = hostname,
                                 .Port = port,
                                 .AuthToken = encryptedToken,
                                 .Os = os,
                                 .IsEnabled = True,
                                 .Notes = "",
                                 .AddedAt = DateTime.UtcNow
                             }
                             db.Nodes.Add(entity)
                             Await db.SaveChangesAsync()

                             BeginInvoke(Sub()
                                             CreatedNodeId = nodeId
                                             DialogResult = DialogResult.OK
                                             Close()
                                         End Sub)
                         End Using
                     Catch ex As Exception
                         Dim errMsg = "Failed to save node:" & vbNewLine & ex.Message
                         BeginInvoke(Sub()
                                         _btnSave.Enabled = True
                                         MessageBox.Show(
                                             errMsg,
                                             "Error",
                                             MessageBoxButtons.OK,
                                             MessageBoxIcon.Error)
                                     End Sub)
                     End Try
                 End Function)
    End Sub

    Private Function ValidateInputs() As Boolean
        If String.IsNullOrWhiteSpace(_txtDisplayName.Text) Then
            MessageBox.Show("Please enter a display name.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning)
            _txtDisplayName.Focus()
            Return False
        End If
        If String.IsNullOrWhiteSpace(_txtHostname.Text) Then
            MessageBox.Show("Please enter the hostname or IP.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning)
            _txtHostname.Focus()
            Return False
        End If
        If String.IsNullOrWhiteSpace(_txtToken.Text) Then
            MessageBox.Show("Please enter the auth token.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning)
            _txtToken.Focus()
            Return False
        End If
        Return True
    End Function

End Class
