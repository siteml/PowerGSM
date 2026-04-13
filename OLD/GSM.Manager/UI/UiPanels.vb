Imports System.Collections.Generic
Imports System.Drawing
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports GSM.Core
Imports GSM.Data
Imports GSM.Plugin
Imports GSM.Node.Api

' ============================================================
'  InstancePanel
'
'  The detail panel shown on the right when an instance is
'  selected in the tree. Shows:
'    - Instance name, game, current state
'    - Player count and player list
'    - Start / Stop / Restart / Kill buttons
'    - Startup warnings (if any)
'    - RCON state and quick command entry
'    - Links to log viewer and automation rules
'
'  State is refreshed two ways:
'    1. Bound from tree selection (full refresh)
'    2. RefreshState(newState) called by MainForm when
'       an InstanceStateChanged event arrives (fast path)
' ============================================================

Public Class InstancePanel
    Inherits UserControl

    Public Property CurrentInstanceId As String

    ' Layout controls
    Private _lblName As Label
    Private _lblGame As Label
    Private _lblState As Label
    Private _lblPlayers As Label
    Private _listPlayers As ListBox
    Private _btnStart As Button
    Private _btnStop As Button
    Private _btnRestart As Button
    Private _btnKill As Button
    Private _btnLogs As Button
    Private _btnRules As Button
    Private _pnlWarnings As Panel
    Private _lblWarnings As Label
    Private _pnlRcon As GroupBox
    Private _txtRconCommand As TextBox
    Private _btnRconSend As Button
    Private _lblRconStatus As Label

    ' State colours
    Private Shared ReadOnly _stateColours As New Dictionary(Of String, Color) From {
        {"Running",          Color.FromArgb(0, 150, 0)},
        {"Starting",         Color.DarkOrange},
        {"Restarting",       Color.DarkOrange},
        {"Stopped",          Color.Gray},
        {"Stopping",         Color.Gray},
        {"Crashed",          Color.Red},
        {"CrashLoopHalted",  Color.DarkRed},
        {"StartFailed",      Color.Red},
        {"InstallationLocked", Color.DarkSlateGray}
    }

    ' Services
    Private _instanceManager As InstanceManager
    Private _pluginRegistry As PluginRegistry
    Private _cts As CancellationTokenSource

    Public Sub New()
        BuildLayout()
        Visible = False
    End Sub

    Private Sub BuildLayout()
        Padding = New Padding(12)
        AutoScroll = True

        ' Instance name and game
        _lblName = New Label With {
            .Font = New Font(Font.FontFamily, 14, FontStyle.Bold),
            .AutoSize = True,
            .Location = New Point(12, 12)
        }
        _lblGame = New Label With {
            .AutoSize = True,
            .ForeColor = Color.Gray,
            .Location = New Point(12, 40)
        }

        ' State badge
        _lblState = New Label With {
            .AutoSize = False,
            .Size = New Size(160, 28),
            .Location = New Point(12, 64),
            .TextAlign = ContentAlignment.MiddleCenter,
            .Font = New Font(Font.FontFamily, 10, FontStyle.Bold),
            .ForeColor = Color.White,
            .BackColor = Color.Gray
        }

        ' Player info
        _lblPlayers = New Label With {
            .AutoSize = True,
            .Location = New Point(12, 104)
        }
        _listPlayers = New ListBox With {
            .Location = New Point(12, 124),
            .Size = New Size(250, 80),
            .BorderStyle = BorderStyle.FixedSingle
        }

        ' Control buttons
        Dim btnY = 216
        _btnStart = MakeButton("▶ Start", btnY, Color.FromArgb(0, 120, 0), AddressOf OnStartClick)
        _btnStop = MakeButton("■ Stop", btnY, Color.FromArgb(180, 0, 0), AddressOf OnStopClick)
        _btnRestart = MakeButton("↺ Restart", btnY, Color.FromArgb(0, 100, 160), AddressOf OnRestartClick)
        _btnKill = MakeButton("✕ Kill", btnY, Color.FromArgb(80, 80, 80), AddressOf OnKillClick)
        LayoutButtons(btnY)

        ' Utility buttons
        _btnLogs = New Button With {
            .Text = "📋 Open Logs",
            .Location = New Point(12, 262),
            .Size = New Size(120, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler _btnLogs.Click, AddressOf OnLogsClick

        _btnRules = New Button With {
            .Text = "⚙ Automation Rules",
            .Location = New Point(140, 262),
            .Size = New Size(150, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler _btnRules.Click, AddressOf OnRulesClick

        ' Warnings panel (hidden when no warnings)
        _pnlWarnings = New Panel With {
            .Location = New Point(12, 300),
            .Size = New Size(500, 60),
            .BackColor = Color.FromArgb(255, 243, 205),
            .BorderStyle = BorderStyle.FixedSingle,
            .Visible = False
        }
        _lblWarnings = New Label With {
            .Dock = DockStyle.Fill,
            .Padding = New Padding(4),
            .ForeColor = Color.FromArgb(133, 100, 4)
        }
        _pnlWarnings.Controls.Add(_lblWarnings)

        ' RCON group
        _pnlRcon = New GroupBox With {
            .Text = "RCON",
            .Location = New Point(12, 370),
            .Size = New Size(500, 80)
        }
        _lblRconStatus = New Label With {
            .Text = "Not available",
            .Location = New Point(8, 20),
            .AutoSize = True,
            .ForeColor = Color.Gray
        }
        _txtRconCommand = New TextBox With {
            .Location = New Point(8, 42),
            .Size = New Size(380, 23),
            .PlaceholderText = "Enter RCON command..."
        }
        _btnRconSend = New Button With {
            .Text = "Send",
            .Location = New Point(396, 42),
            .Size = New Size(72, 23),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler _btnRconSend.Click, AddressOf OnRconSendClick
        AddHandler _txtRconCommand.KeyDown, AddressOf OnRconKeyDown
        _pnlRcon.Controls.AddRange({_lblRconStatus, _txtRconCommand, _btnRconSend})

        Controls.AddRange({_lblName, _lblGame, _lblState,
                            _lblPlayers, _listPlayers,
                            _btnStart, _btnStop, _btnRestart, _btnKill,
                            _btnLogs, _btnRules,
                            _pnlWarnings, _pnlRcon})
    End Sub

    Private Function MakeButton(text As String, y As Integer,
                                  colour As Color,
                                  handler As EventHandler) As Button
        Dim btn As New Button With {
            .Text = text,
            .Size = New Size(88, 32),
            .FlatStyle = FlatStyle.Flat,
            .ForeColor = Color.White,
            .BackColor = colour
        }
        btn.FlatAppearance.BorderSize = 0
        AddHandler btn.Click, handler
        Return btn
    End Function

    Private Sub LayoutButtons(y As Integer)
        _btnStart.Location = New Point(12, y)
        _btnStop.Location = New Point(106, y)
        _btnRestart.Location = New Point(200, y)
        _btnKill.Location = New Point(294, y)
    End Sub

    ' ---- Binding ----

    Public Async Sub Bind(instanceId As String,
                    displayName As String,
                    gameId As String,
                    instanceManager As InstanceManager,
                    pluginRegistry As PluginRegistry)

        CurrentInstanceId = instanceId
        _instanceManager = instanceManager
        _pluginRegistry = pluginRegistry

        _lblName.Text = displayName
        _lblGame.Text = $"Game: {If(pluginRegistry.GetPlugin(gameId)?.DisplayName, gameId)}"
        _lblState.Text = "Loading..."
        _lblState.BackColor = Color.Gray
        _listPlayers.Items.Clear()
        _lblPlayers.Text = "Players: ..."

        If _cts IsNot Nothing Then _cts.Cancel()
        _cts = New CancellationTokenSource()

        Task.Run(Async Function()
                     Await RefreshFromNodeAsync(_cts.Token)
                 End Function)
    End Sub

    Private Async Function RefreshFromNodeAsync(cancellation As CancellationToken) As Task
        Try
            Dim metrics = Await _instanceManager.GetMetricsAsync(
                CurrentInstanceId, cancellation)

            BeginInvoke(Sub() ApplyMetrics(metrics))
        Catch ex As OperationCanceledException
        Catch ex As Exception
            BeginInvoke(Sub()
                            _lblState.Text = "Unreachable"
                            _lblState.BackColor = Color.Gray
                        End Sub)
        End Try
    End Function

    Private Sub ApplyMetrics(metrics As InstanceMetricsResponse)
        ' State label with colour
        Dim stateStr = metrics.State.ToString()
        _lblState.Text = stateStr
        _lblState.BackColor = If(_stateColours.ContainsKey(stateStr),
                                  _stateColours(stateStr), Color.Gray)

        ' Players
        _lblPlayers.Text = $"Players: {metrics.PlayerCount}"
        _listPlayers.Items.Clear()
        For Each p In metrics.Players
            _listPlayers.Items.Add(p.Name)
        Next

        ' Button states
        Dim isRunning = metrics.State = InstanceState.Running OrElse
                        metrics.State = InstanceState.Starting
        _btnStart.Enabled = Not isRunning
        _btnStop.Enabled = isRunning
        _btnRestart.Enabled = isRunning
        _btnKill.Enabled = isRunning

        ' RCON
        Dim rconReady = metrics.RconState = RconState.Connected
        _lblRconStatus.Text = $"RCON: {metrics.RconState}"
        _lblRconStatus.ForeColor = If(rconReady, Color.DarkGreen, Color.Gray)
        _txtRconCommand.Enabled = rconReady
        _btnRconSend.Enabled = rconReady
    End Sub

    ' Fast path called by MainForm when a state change event arrives.
    Public Sub RefreshState(newState As InstanceState)
        Dim stateStr = newState.ToString()
        _lblState.Text = stateStr
        _lblState.BackColor = If(_stateColours.ContainsKey(stateStr),
                                  _stateColours(stateStr), Color.Gray)
        Dim isRunning = newState = InstanceState.Running OrElse
                        newState = InstanceState.Starting
        _btnStart.Enabled = Not isRunning
        _btnStop.Enabled = isRunning
        _btnRestart.Enabled = isRunning
        _btnKill.Enabled = isRunning
    End Sub

    ' ---- Button handlers ----

    Private Async Sub OnStartClick(sender As Object, e As EventArgs)
        RunCommand("Starting...", Async Function(ct)
            Await _instanceManager.StartInstanceAsync(CurrentInstanceId, ct)
        End Function)
    End Sub

    Private Async Sub OnStopClick(sender As Object, e As EventArgs)
        RunCommand("Stopping...", Async Function(ct)
            Await _instanceManager.StopInstanceAsync(CurrentInstanceId, True, ct)
        End Function)
    End Sub

    Private Async Sub OnRestartClick(sender As Object, e As EventArgs)
        RunCommand("Restarting...", Async Function(ct)
            Await _instanceManager.RestartInstanceAsync(CurrentInstanceId, True, ct)
        End Function)
    End Sub

    Private Async Sub OnKillClick(sender As Object, e As EventArgs)
        If MessageBox.Show(
                "Force kill this instance? The process will be terminated immediately.",
                "Confirm Kill",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) <> DialogResult.Yes Then Return

        RunCommand("Killing...", Async Function(ct)
            Await _instanceManager.StopInstanceAsync(CurrentInstanceId, False, ct)
        End Function)
    End Sub

    Private Async Sub OnRconSendClick(sender As Object, e As EventArgs)
        Dim cmd = _txtRconCommand.Text.Trim()
        If String.IsNullOrEmpty(cmd) Then Return
        _txtRconCommand.Clear()
        Task.Run(Async Function()
                     Try
                         Dim result = Await _instanceManager.SendRconCommandAsync(
                             CurrentInstanceId, cmd, CancellationToken.None)
                         If result.Success Then
                             MessageBox.Show(result.Response, "RCON Response",
                                 MessageBoxButtons.OK, MessageBoxIcon.Information)
                         Else
                             MessageBox.Show(result.ErrorMessage, "RCON Error",
                                 MessageBoxButtons.OK, MessageBoxIcon.Error)
                         End If
                     Catch ex As Exception
                         MessageBox.Show(ex.Message, "RCON Error",
                             MessageBoxButtons.OK, MessageBoxIcon.Error)
                     End Try
                 End Function)
    End Sub

    Private Sub OnRconKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Enter Then OnRconSendClick(sender, e)
    End Sub

    Private Sub OnLogsClick(sender As Object, e As EventArgs)
        Dim mainForm = TryCast(ParentForm, MainForm)
        If mainForm IsNot Nothing Then mainForm.OpenLogViewer(CurrentInstanceId, _lblName.Text)
    End Sub

    Private Sub OnRulesClick(sender As Object, e As EventArgs)
        Using dlg As New AutomationRulesForm(
                CurrentInstanceId, _lblName.Text,
                TryCast(ParentForm, MainForm)?.AutomationEngine)
            dlg.ShowDialog(ParentForm)
        End Using
    End Sub

    ' Runs a command on the thread pool, disabling buttons while it runs.
    Private Async Sub RunCommand(statusText As String,
                            command As Func(Of CancellationToken, Task))
        SetButtonsEnabled(False)
        Task.Run(Async Function()
                     Try
                         Await command(CancellationToken.None)
                     Catch ex As Exception
                         BeginInvoke(Sub()
                                         MessageBox.Show(ex.Message, "Error",
                                             MessageBoxButtons.OK, MessageBoxIcon.Error)
                                     End Sub)
                     End Try

                     ' Always re-enable buttons after completion or failure.
                     Await Task.Delay(1000)   ' Brief pause before re-enable
                     BeginInvoke(Sub()
                                     SetButtonsEnabled(True)
                                     Task.Run(Async Function()
                                         Await RefreshFromNodeAsync(CancellationToken.None)
                                     End Function)
                                 End Sub)
                 End Function)
    End Sub

    Private Sub SetButtonsEnabled(enabled As Boolean)
        _btnStart.Enabled = enabled
        _btnStop.Enabled = enabled
        _btnRestart.Enabled = enabled
        _btnKill.Enabled = enabled
    End Sub

End Class


' ============================================================
'  LogViewerForm
'
'  A modeless window that streams live log output for one
'  instance via the node's SSE endpoint.
'
'  Features:
'    - Live tail via IAsyncEnumerable(Of LogLine)
'    - Source filter dropdown (stdout, logfile, all)
'    - Auto-scroll toggle
'    - Copy selected lines
'    - Clear buffer
'    - Search/filter textbox (client-side, no re-fetch)
'
'  One instance per running log viewer window.
'  Opening the same instance twice brings the existing window
'  to front (enforced in MainForm.OpenLogViewer).
' ============================================================

Public Class LogViewerForm
    Inherits Form

    Public ReadOnly Property InstanceId As String

    Private ReadOnly _instanceManager As InstanceManager
    Private _cts As New CancellationTokenSource()

    ' Controls
    Private _txtLog As RichTextBox
    Private _cboSource As ComboBox
    Private _chkAutoScroll As CheckBox
    Private _txtSearch As TextBox
    Private _btnClear As Button
    Private _lblStatus As Label

    ' Line colours by source
    Private Shared ReadOnly _sourceColours As New Dictionary(Of String, Color) From {
        {"stdout",  Color.White},
        {"stderr",  Color.FromArgb(255, 180, 180)},
        {"logfile", Color.FromArgb(200, 220, 255)},
        {"install", Color.FromArgb(200, 255, 200)}
    }

    Public Sub New(instanceId As String,
                   displayName As String,
                   instanceManager As InstanceManager)
        InstanceId = instanceId
        _instanceManager = instanceManager

        Text = $"Logs — {displayName}"
        Size = New Size(900, 600)
        MinimumSize = New Size(600, 400)
        StartPosition = FormStartPosition.CenterParent

        BuildLayout()
        StartStreaming()
    End Sub

    Private Sub BuildLayout()
        ' Toolbar
        Dim toolbar As New ToolStrip With {.GripStyle = ToolStripGripStyle.Hidden}

        Dim sourceLabel As New ToolStripLabel("Source:")
        Dim sourceCombo As New ToolStripControlHost(
            New ComboBox With {
                .DropDownStyle = ComboBoxStyle.DropDownList,
                .Width = 100
            })
        _cboSource = CType(sourceCombo.Control, ComboBox)
        _cboSource.Items.AddRange({"All", "stdout", "stderr", "logfile", "install"})
        _cboSource.SelectedIndex = 0

        Dim searchLabel As New ToolStripLabel("Filter:")
        Dim searchHost As New ToolStripControlHost(
            New TextBox With {.Width = 160, .PlaceholderText = "Filter text..."})
        _txtSearch = CType(searchHost.Control, TextBox)

        Dim scrollCheck As New ToolStripControlHost(
            New CheckBox With {.Text = "Auto-scroll", .Checked = True})
        _chkAutoScroll = CType(scrollCheck.Control, CheckBox)

        Dim clearBtn As New ToolStripButton("Clear") With {.DisplayStyle = ToolStripItemDisplayStyle.Text}
        AddHandler clearBtn.Click, Sub(s, e) _txtLog.Clear()

        toolbar.Items.AddRange({sourceLabel, sourceCombo,
                                 New ToolStripSeparator(),
                                 searchLabel, searchHost,
                                 New ToolStripSeparator(),
                                 scrollCheck, clearBtn})

        ' Log area - RichTextBox lets us colour lines by source
        _txtLog = New RichTextBox With {
            .Dock = DockStyle.Fill,
            .BackColor = Color.FromArgb(15, 15, 15),
            .ForeColor = Color.White,
            .Font = New Font("Consolas", 9),
            .ReadOnly = True,
            .WordWrap = False,
            .ScrollBars = RichTextBoxScrollBars.Both
        }

        ' Status bar
        _lblStatus = New Label With {
            .Dock = DockStyle.Bottom,
            .Height = 22,
            .Text = "Connecting...",
            .ForeColor = Color.Gray,
            .Padding = New Padding(4, 0, 0, 0)
        }

        Controls.AddRange({toolbar, _txtLog, _lblStatus})
    End Sub

    Private Async Sub StartStreaming()
        Task.Run(Async Function()
                     Try
                         ' Load recent history first.
                         Dim recent = Await _instanceManager.GetLogsAsync(
                             InstanceId, 200, _cts.Token)

                         BeginInvoke(Sub()
                                         _lblStatus.Text = $"Showing last {recent.Lines.Count} lines. Streaming..."
                                         For Each line In recent.Lines
                                             AppendLine(line.SourceId,
                                                         line.Timestamp,
                                                         line.Content)
                                         Next
                                     End Sub)

                         ' Stream live lines.
                         Dim fromIndex = If(recent.Lines.Any(),
                                            recent.Lines.Last().LineIndex + 1, -1L)

                         Await _instanceManager.StreamLogsAsync(
                                 InstanceId, fromIndex,
                                 Sub(line)
                                     If _cts.IsCancellationRequested Then Return
                                     BeginInvoke(Sub() AppendLine(line.SourceId,
                                                                   line.Timestamp,
                                                                   line.Content))
                                 End Sub,
                                 _cts.Token)

                     Catch ex As OperationCanceledException
                         ' Window closed - normal.
                     Catch ex As Exception
                         BeginInvoke(Sub()
                                         _lblStatus.Text = "Stream error: " & ex.Message
                                         _lblStatus.ForeColor = Color.Red
                                     End Sub)
                     End Try
                 End Function)
    End Sub

    Private Sub AppendLine(sourceId As String,
                            timestamp As DateTime,
                            content As String)
        ' Apply source filter.
        Dim sourceFilter = _cboSource.SelectedItem?.ToString()
        If sourceFilter <> "All" AndAlso sourceFilter <> sourceId Then Return

        ' Apply text filter.
        Dim textFilter = _txtSearch.Text.Trim()
        If Not String.IsNullOrEmpty(textFilter) AndAlso
           content.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0 Then Return

        ' Colour by source.
        Dim colour = If(_sourceColours.ContainsKey(sourceId),
                        _sourceColours(sourceId), Color.White)

        _txtLog.SuspendLayout()
        _txtLog.SelectionStart = _txtLog.TextLength
        _txtLog.SelectionLength = 0
        _txtLog.SelectionColor = Color.FromArgb(120, 120, 120)
        _txtLog.AppendText($"[{timestamp:HH:mm:ss}] ")
        _txtLog.SelectionColor = colour
        _txtLog.AppendText($"{content}{vbNewLine}")
        _txtLog.ResumeLayout()

        If _chkAutoScroll.Checked Then
            _txtLog.ScrollToCaret()
        End If
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        MyBase.OnFormClosing(e)
        _cts.Cancel()
    End Sub

End Class


' ============================================================
'  SchemaFormBuilder
'
'  The key to the plugin-driven UI. Reads a list of
'  ConfigFieldDescriptor objects (declared by the plugin)
'  and generates WinForms controls for each field.
'
'  Supported field types:
'    Text, Integer, Boolean, Choice, FilePath, DirectoryPath,
'    Password, CredentialPicker, SteamCredentialPicker
'
'  Usage:
'    Dim fields = plugin.GetInstanceConfigSchema()
'    Dim builder = New SchemaFormBuilder(fields, existingJson)
'    panel.Controls.AddRange(builder.BuildControls())
'    ...
'    Dim resultJson = builder.GetCurrentJson()
'
'  This means adding a new game never requires a new UI form.
'  Drop in a plugin → the config form generates itself.
' ============================================================

Public Class SchemaFormBuilder

    Private ReadOnly _fields As IReadOnlyList(Of ConfigFieldDescriptor)
    Private ReadOnly _controls As New Dictionary(Of String, Control)()
    Private ReadOnly _credentialService As CredentialService
    Private ReadOnly _pluginRegistry As PluginRegistry

    ' Loaded when CredentialPicker fields are built.
    Private _realmCredentials As List(Of RealmCredentialEntity)
    Private _steamCredentials As List(Of SteamCredentialEntity)

    Public Sub New(fields As IReadOnlyList(Of ConfigFieldDescriptor),
                   Optional initialJson As String = "{}",
                   Optional credentialService As CredentialService = Nothing,
                   Optional pluginRegistry As PluginRegistry = Nothing)
        _fields = fields
        _credentialService = credentialService
        _pluginRegistry = pluginRegistry

        ' Parse initial values.
        Dim initial As Dictionary(Of String, Object) = Nothing
        If Not String.IsNullOrEmpty(initialJson) Then
            Try
                initial = System.Text.Json.JsonSerializer.Deserialize(
                    Of Dictionary(Of String, Object))(initialJson)
            Catch
            End Try
        End If
        If initial Is Nothing Then initial = New Dictionary(Of String, Object)()
        _initialValues = initial
    End Sub

    Private ReadOnly _initialValues As Dictionary(Of String, Object)

    ' Builds and returns a Panel containing all controls.
    ' The Panel can be added to any form or container.
    Public Function BuildPanel() As Panel
        Dim panel As New Panel With {
            .AutoScroll = True,
            .Dock = DockStyle.Fill
        }

        Dim y = 8
        Const LabelWidth = 180
        Const ControlLeft = 188
        Const ControlWidth = 300
        Const RowHeight = 52

        For Each field In _fields
            ' Label
            Dim lbl As New Label With {
                .Text = If(field.IsRequired, field.Label & " *", field.Label),
                .Location = New Point(8, y + 4),
                .Size = New Size(LabelWidth, 20),
                .Font = If(field.IsRequired,
                            New Font(SystemFonts.DefaultFont, FontStyle.Bold),
                            SystemFonts.DefaultFont)
            }
            panel.Controls.Add(lbl)

            ' Tooltip for description
            If Not String.IsNullOrEmpty(field.Description) Then
                Dim tip As New ToolTip()
                tip.SetToolTip(lbl, field.Description)
            End If

            ' Control
            Dim ctrl = BuildControl(field, ControlLeft, y, ControlWidth)
            panel.Controls.Add(ctrl)
            _controls(field.Key) = ctrl

            ' Validation indicator
            If field.IsRequired Then
                Dim req As New Label With {
                    .Text = "●",
                    .ForeColor = Color.Red,
                    .Location = New Point(ControlLeft + ControlWidth + 4, y + 4),
                    .AutoSize = True,
                    .Visible = String.IsNullOrEmpty(GetControlValue(ctrl))
                }
                panel.Controls.Add(req)
                ' Hide the indicator when the field is filled.
                AddHandler ctrl.TextChanged, Sub(s, e)
                    req.Visible = String.IsNullOrEmpty(GetControlValue(ctrl))
                End Sub
            End If

            y += RowHeight
        Next

        panel.Height = y + 8
        Return panel
    End Function

    Private Function BuildControl(field As ConfigFieldDescriptor,
                                   left As Integer,
                                   top As Integer,
                                   width As Integer) As Control

        Dim existing = If(_initialValues.GetValueOrDefault(field.Key)?.ToString(), field.DefaultValue)

        Select Case field.FieldType

            Case ConfigFieldType.BooleanField
                Dim chk As New CheckBox With {
                    .Location = New Point(left, top + 4),
                    .AutoSize = True,
                    .Checked = existing = "true" OrElse existing = "True" OrElse existing = "1"
                }
                Return chk

            Case ConfigFieldType.Choice
                Dim cbo As New ComboBox With {
                    .Location = New Point(left, top),
                    .Size = New Size(width, 23),
                    .DropDownStyle = ComboBoxStyle.DropDownList
                }
                For Each choice In If(field.Choices, New List(Of String)())
                    cbo.Items.Add(choice)
                Next
                Dim idx = cbo.Items.IndexOf(existing)
                cbo.SelectedIndex = If(idx >= 0, idx, 0)
                Return cbo

            Case ConfigFieldType.IntegerField
                Dim num As New NumericUpDown With {
                    .Location = New Point(left, top),
                    .Size = New Size(width, 23),
                    .Minimum = If(field.MinValue.HasValue, field.MinValue.Value, 0),
                    .Maximum = If(field.MaxValue.HasValue, field.MaxValue.Value, 65535),
                    .DecimalPlaces = 0
                }
                Dim parsed As Integer
                If Integer.TryParse(existing, parsed) Then num.Value = parsed
                Return num

            Case ConfigFieldType.Password
                Dim pwd As New TextBox With {
                    .Location = New Point(left, top),
                    .Size = New Size(width, 23),
                    .PasswordChar = "●"c,
                    .Text = existing
                }
                Return pwd

            Case ConfigFieldType.FilePath
                Return BuildPathControl(left, top, width, existing, isDirectory:=False)

            Case ConfigFieldType.DirectoryPath
                Return BuildPathControl(left, top, width, existing, isDirectory:=True)

            Case ConfigFieldType.CredentialPicker
                Return BuildCredentialPicker(left, top, width, existing,
                                              isSteam:=False)

            Case ConfigFieldType.SteamCredentialPicker
                Return BuildCredentialPicker(left, top, width, existing,
                                              isSteam:=True)

            Case Else   ' Text
                Dim txt As New TextBox With {
                    .Location = New Point(left, top),
                    .Size = New Size(width, 23),
                    .Text = existing
                }
                Return txt

        End Select
    End Function

    Private Function BuildPathControl(left As Integer, top As Integer,
                                       width As Integer, initial As String,
                                       isDirectory As Boolean) As Control
        Dim pnl As New Panel With {
            .Location = New Point(left, top),
            .Size = New Size(width + 30, 23)
        }
        Dim txt As New TextBox With {
            .Location = New Point(0, 0),
            .Size = New Size(width - 4, 23),
            .Text = initial
        }
        Dim btn As New Button With {
            .Text = "…",
            .Location = New Point(width - 2, 0),
            .Size = New Size(30, 23),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler btn.Click, Sub(s, e)
            If isDirectory Then
                Using dlg As New FolderBrowserDialog()
                    dlg.SelectedPath = txt.Text
                    If dlg.ShowDialog() = DialogResult.OK Then
                        txt.Text = dlg.SelectedPath
                    End If
                End Using
            Else
                Using dlg As New OpenFileDialog()
                    dlg.FileName = txt.Text
                    If dlg.ShowDialog() = DialogResult.OK Then
                        txt.Text = dlg.FileName
                    End If
                End Using
            End If
        End Sub
        pnl.Controls.AddRange({txt, btn})
        ' Tag the panel so GetControlValue can find the textbox inside it.
        pnl.Tag = txt
        Return pnl
    End Function

    Private Function BuildCredentialPicker(left As Integer, top As Integer,
                                            width As Integer, initial As String,
                                            isSteam As Boolean) As Control
        Dim cbo As New ComboBox With {
            .Location = New Point(left, top),
            .Size = New Size(width - 36, 23),
            .DropDownStyle = ComboBoxStyle.DropDownList
        }
        Dim btnNew As New Button With {
            .Text = "+",
            .Location = New Point(left + width - 34, top),
            .Size = New Size(34, 23),
            .FlatStyle = FlatStyle.Flat
        }

        ' Populate asynchronously.
        Task.Run(Async Function()
                     If isSteam AndAlso _credentialService IsNot Nothing Then
                         Dim creds = Await _credentialService.ListSteamCredentialsAsync(CancellationToken.None)
                         cbo.BeginInvoke(Sub()
                                             cbo.Items.Add(New CredentialItem("", "(None / Anonymous)"))
                                             For Each c In creds
                                                 cbo.Items.Add(New CredentialItem(c.CredentialId, c.DisplayName))
                                             Next
                                             SelectById(cbo, initial)
                                         End Sub)
                     ElseIf Not isSteam AndAlso _credentialService IsNot Nothing Then
                         Dim creds = Await _credentialService.ListRealmCredentialsAsync("", CancellationToken.None)
                         cbo.BeginInvoke(Sub()
                                             cbo.Items.Add(New CredentialItem("", "(None)"))
                                             For Each c In creds
                                                 cbo.Items.Add(New CredentialItem(c.CredentialId, c.DisplayName))
                                             Next
                                             SelectById(cbo, initial)
                                         End Sub)
                     End If
                 End Function)

        Dim pnl As New Panel With {
            .Location = New Point(left, top),
            .Size = New Size(width, 23)
        }
        pnl.Controls.AddRange({cbo, btnNew})
        pnl.Tag = cbo
        Return pnl
    End Function

    Private Shared Sub SelectById(cbo As ComboBox, id As String)
        For i = 0 To cbo.Items.Count - 1
            Dim item = TryCast(cbo.Items(i), CredentialItem)
            If item IsNot Nothing AndAlso item.Id = id Then
                cbo.SelectedIndex = i
                Return
            End If
        Next
        If cbo.Items.Count > 0 Then cbo.SelectedIndex = 0
    End Sub

    ' ---- Read current values ----

    ' Returns the current values as a JSON string.
    ' Call this when the user clicks OK to save the form.
    Public Function GetCurrentJson() As String
        Dim result As New Dictionary(Of String, Object)()
        For Each kvp In _controls
            result(kvp.Key) = GetControlValue(kvp.Value)
        Next
        Return System.Text.Json.JsonSerializer.Serialize(result)
    End Function

    ' Returns True if all required fields have values.
    Public Function IsValid(ByRef errorMessage As String) As Boolean
        For Each field In _fields
            If Not field.IsRequired Then Continue For
            Dim ctrl As Control = Nothing
            If Not _controls.TryGetValue(field.Key, ctrl) Then Continue For
            If String.IsNullOrWhiteSpace(GetControlValue(ctrl)) Then
                errorMessage = $"'{field.Label}' is required."
                Return False
            End If
        Next
        errorMessage = String.Empty
        Return True
    End Function

    Private Shared Function GetControlValue(ctrl As Control) As String
        ' Panel wrappers (path, credential picker) store the real control in .Tag
        If TypeOf ctrl Is Panel AndAlso ctrl.Tag IsNot Nothing Then
            Return GetControlValue(CType(ctrl.Tag, Control))
        End If
        If TypeOf ctrl Is CheckBox Then
            Return If(CType(ctrl, CheckBox).Checked, "true", "false")
        End If
        If TypeOf ctrl Is ComboBox Then
            Dim item = TryCast(CType(ctrl, ComboBox).SelectedItem, CredentialItem)
            If item IsNot Nothing Then Return item.Id
            Return If(CType(ctrl, ComboBox).SelectedItem?.ToString(), "")
        End If
        If TypeOf ctrl Is NumericUpDown Then
            Return CType(ctrl, NumericUpDown).Value.ToString()
        End If
        Return ctrl.Text
    End Function

    ' Helper class for credential picker items.
    Private Class CredentialItem
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
'  WelcomePanel
'  Shown when nothing is selected in the tree.
' ============================================================

Public Class WelcomePanel
    Inherits UserControl

    Public Sub New()
        Dim lbl As New Label With {
            .Text = "Select a node, installation, or instance from the tree.",
            .Dock = DockStyle.Fill,
            .TextAlign = ContentAlignment.MiddleCenter,
            .ForeColor = Color.Gray,
            .Font = New Font(SystemFonts.DefaultFont.FontFamily, 11)
        }
        Controls.Add(lbl)
    End Sub
End Class


' ============================================================
'  NodePanel
'  Shown when a node is selected in the tree.
'  Displays system info and online/offline status.
' ============================================================

Public Class NodePanel
    Inherits UserControl

    Private _lblName As Label
    Private _lblStatus As Label
    Private _lblInfo As Label
    Private _btnAddInstall As Button

    Private _nodeId As String

    Public Sub New()
        Dim lbl As New Label With {
            .Text = "Node",
            .Font = New Font(Font.FontFamily, 14, FontStyle.Bold),
            .AutoSize = True,
            .Location = New Point(12, 12)
        }
        _lblName = lbl

        _lblStatus = New Label With {
            .AutoSize = True,
            .Location = New Point(12, 40),
            .ForeColor = Color.Gray
        }
        _lblInfo = New Label With {
            .AutoSize = True,
            .Location = New Point(12, 64),
            .ForeColor = Color.Gray,
            .MaximumSize = New Size(500, 0)
        }
        _btnAddInstall = New Button With {
            .Text = "Add Installation...",
            .Location = New Point(12, 120),
            .Size = New Size(150, 28),
            .FlatStyle = FlatStyle.Flat
        }
        AddHandler _btnAddInstall.Click, AddressOf OnAddInstallClick

        Controls.AddRange({_lblName, _lblStatus, _lblInfo, _btnAddInstall})
        Padding = New Padding(12)
    End Sub

    Public Sub Bind(nodeId As String, displayName As String)
        _nodeId = nodeId
        _lblName.Text = displayName
        _lblStatus.Text = "Checking..."
        Task.Run(AddressOf RefreshAsync)
    End Sub

    Private Async Function RefreshAsync() As Task
        ' Try to get health from the node.
        ' NodeHttpClientFactory is not available here directly -
        ' in production this would be injected. Shown conceptually.
        Await Task.Delay(100)   ' Placeholder
        BeginInvoke(Sub() _lblStatus.Text = "Online")
    End Function

    Private Sub OnAddInstallClick(sender As Object, e As EventArgs)
        ' Open the new installation wizard.
        ' Passes the nodeId so it knows which node to target.
        Dim mainForm = TryCast(ParentForm, MainForm)
        If mainForm Is Nothing Then Return
        Using dlg As New NewInstallationForm(
                _nodeId,
                mainForm.PluginRegistry,
                mainForm.InstallationManager,
                mainForm.CredentialService)
            If dlg.ShowDialog(ParentForm) = DialogResult.OK Then
                Dim _ignore = mainForm.LoadTreeAsync()
            End If
        End Using
    End Sub

End Class
