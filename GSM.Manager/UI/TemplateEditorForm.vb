Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports GSM.Notification

' ============================================================
'  TemplateEditorForm — lets the user override the default
'  Discord embed description on a per-event-type basis.
'
'  Templates use {TokenName} substitution. Clicking a token in
'  the palette inserts it at the current caret position in the
'  template textbox. Leaving a template blank means "use default"
'  (which falls back to the structured field list).
' ============================================================

Namespace GSM.Manager.UI

    Public Class TemplateEditorForm
        Inherits Form

        ' Event types we expose template overrides for. Must match
        ' the events surfaced in NotificationsForm's checkbox list.
        Private Shared ReadOnly EditableEvents As NotificationEventType() = {
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

        ' Tokens available in templates. Keep in sync with
        ' DiscordEmbedBuilder.ApplyTokens.
        Private Shared ReadOnly AvailableTokens As String() = {
            "{NodeName}", "{InstanceName}", "{InstallationName}", "{GameName}",
            "{PlayerName}", "{PlayerCount}", "{MaxPlayers}",
            "{RuleName}", "{ErrorMessage}",
            "{EventType}", "{Timestamp}", "{Message}",
            "{PID}", "{ExitCode}", "{BuildId}",
            "{CrashCount}", "{WindowMinutes}"
        }

        Private _eventCombo As ComboBox
        Private _templateTextBox As TextBox
        Private _tokenList As ListBox
        Private _previewLabel As Label
        Private _useDefaultCheckBox As CheckBox
        Private _saveButton As Button
        Private _cancelButton As Button

        Private _working As Dictionary(Of NotificationEventType, String)
        Private _suppressEvents As Boolean = False

        Public ReadOnly Property ResultTemplates As Dictionary(Of NotificationEventType, String)
            Get
                ' Strip blanks — "use default" means not in the dict.
                Return _working.Where(Function(kv) Not String.IsNullOrWhiteSpace(kv.Value)).
                                 ToDictionary(Function(kv) kv.Key, Function(kv) kv.Value)
            End Get
        End Property

        Public Sub New(existing As Dictionary(Of NotificationEventType, String))
            FormIconHelper.ApplyTo(Me)
            _working = If(existing Is Nothing,
                           New Dictionary(Of NotificationEventType, String),
                           New Dictionary(Of NotificationEventType, String)(existing))
            InitializeControls()
            If _eventCombo.Items.Count > 0 Then _eventCombo.SelectedIndex = 0
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Customize Message Templates"
            Me.Size = New Size(780, 560)
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 12
            Dim eventLabel As New Label() With {
                .Text = "Event type:", .Location = New Point(16, y + 4), .AutoSize = True}
            Me.Controls.Add(eventLabel)
            _eventCombo = New ComboBox() With {
                .Location = New Point(100, y), .Size = New Size(300, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            For Each evt In EditableEvents
                _eventCombo.Items.Add(New EventComboItem(evt))
            Next
            AddHandler _eventCombo.SelectedIndexChanged, AddressOf OnEventChanged
            Me.Controls.Add(_eventCombo)
            y += 32

            _useDefaultCheckBox = New CheckBox() With {
                .Text = "Use default layout (structured fields, no custom text)",
                .Location = New Point(100, y), .AutoSize = True
            }
            AddHandler _useDefaultCheckBox.CheckedChanged, AddressOf OnUseDefaultChanged
            Me.Controls.Add(_useDefaultCheckBox)
            y += 28

            Dim tplLabel As New Label() With {
                .Text = "Template:", .Location = New Point(16, y), .AutoSize = True,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold)
            }
            Me.Controls.Add(tplLabel)
            y += 22

            _templateTextBox = New TextBox() With {
                .Location = New Point(16, y),
                .Size = New Size(500, 180),
                .Multiline = True,
                .ScrollBars = ScrollBars.Vertical,
                .AcceptsReturn = True,
                .Font = New Font("Consolas", 9)
            }
            AddHandler _templateTextBox.TextChanged, AddressOf OnTemplateChanged
            Me.Controls.Add(_templateTextBox)

            Dim paletteLabel As New Label() With {
                .Text = "Tokens (click to insert):",
                .Location = New Point(540, y - 22), .AutoSize = True,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold)
            }
            Me.Controls.Add(paletteLabel)
            _tokenList = New ListBox() With {
                .Location = New Point(540, y),
                .Size = New Size(210, 180)
            }
            For Each tok In AvailableTokens
                _tokenList.Items.Add(tok)
            Next
            AddHandler _tokenList.DoubleClick, AddressOf OnTokenDoubleClicked
            Me.Controls.Add(_tokenList)
            y += 190

            Dim previewHdr As New Label() With {
                .Text = "Preview (sample values):",
                .Location = New Point(16, y), .AutoSize = True,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold)
            }
            Me.Controls.Add(previewHdr)
            y += 22

            _previewLabel = New Label() With {
                .Location = New Point(16, y),
                .Size = New Size(734, 100),
                .BorderStyle = BorderStyle.FixedSingle,
                .BackColor = Color.White,
                .Padding = New Padding(8),
                .Font = New Font("Segoe UI", 9),
                .TextAlign = ContentAlignment.TopLeft
            }
            Me.Controls.Add(_previewLabel)

            Dim footer As New Panel() With {.Dock = DockStyle.Bottom, .Height = 48, .Padding = New Padding(8)}
            _saveButton = New Button() With {.Text = "OK", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            _saveButton.DialogResult = DialogResult.OK
            _cancelButton = New Button() With {.Text = "Cancel", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            _cancelButton.DialogResult = DialogResult.Cancel
            footer.Controls.Add(_saveButton)
            footer.Controls.Add(_cancelButton)
            Me.Controls.Add(footer)
            Me.AcceptButton = _saveButton
            Me.CancelButton = _cancelButton
        End Sub

        Private Sub OnEventChanged(sender As Object, e As EventArgs)
            Dim item = TryCast(_eventCombo.SelectedItem, EventComboItem)
            If item Is Nothing Then Return
            _suppressEvents = True
            Try
                Dim current As String = Nothing
                _working.TryGetValue(item.EventType, current)
                If String.IsNullOrEmpty(current) Then
                    _useDefaultCheckBox.Checked = True
                    _templateTextBox.Text = ""
                    _templateTextBox.Enabled = False
                Else
                    _useDefaultCheckBox.Checked = False
                    _templateTextBox.Text = current
                    _templateTextBox.Enabled = True
                End If
            Finally
                _suppressEvents = False
            End Try
            UpdatePreview()
        End Sub

        Private Sub OnUseDefaultChanged(sender As Object, e As EventArgs)
            If _suppressEvents Then Return
            _templateTextBox.Enabled = Not _useDefaultCheckBox.Checked
            If _useDefaultCheckBox.Checked Then
                ' Clear template for this event; "use default" path.
                Dim item = TryCast(_eventCombo.SelectedItem, EventComboItem)
                If item IsNot Nothing Then _working.Remove(item.EventType)
                _templateTextBox.Text = ""
            End If
            UpdatePreview()
        End Sub

        Private Sub OnTemplateChanged(sender As Object, e As EventArgs)
            If _suppressEvents Then Return
            Dim item = TryCast(_eventCombo.SelectedItem, EventComboItem)
            If item Is Nothing Then Return
            If String.IsNullOrWhiteSpace(_templateTextBox.Text) Then
                _working.Remove(item.EventType)
            Else
                _working(item.EventType) = _templateTextBox.Text
            End If
            UpdatePreview()
        End Sub

        Private Sub OnTokenDoubleClicked(sender As Object, e As EventArgs)
            If Not _templateTextBox.Enabled Then Return
            Dim tok = TryCast(_tokenList.SelectedItem, String)
            If String.IsNullOrEmpty(tok) Then Return
            Dim caret = _templateTextBox.SelectionStart
            _templateTextBox.Text = _templateTextBox.Text.Insert(caret, tok)
            _templateTextBox.SelectionStart = caret + tok.Length
            _templateTextBox.Focus()
        End Sub

        Private Sub UpdatePreview()
            If _useDefaultCheckBox.Checked Then
                _previewLabel.Text = "(Using default structured layout — the embed will show " &
                                      "labeled fields for the event's context, with no custom body text.)"
                _previewLabel.ForeColor = Color.DimGray
                Return
            End If
            If String.IsNullOrWhiteSpace(_templateTextBox.Text) Then
                _previewLabel.Text = "(Empty template — defaults will be used at runtime.)"
                _previewLabel.ForeColor = Color.DimGray
                Return
            End If

            ' Simple preview: substitute sample values.
            Dim sample As New Dictionary(Of String, String) From {
                {"{NodeName}", "local-node"},
                {"{InstanceName}", "Yellow Dunes"},
                {"{InstallationName}", "Last Oasis"},
                {"{GameName}", "Last Oasis"},
                {"{PlayerName}", "Avery"},
                {"{PlayerCount}", "4"},
                {"{MaxPlayers}", "40"},
                {"{RuleName}", "(no rule)"},
                {"{ErrorMessage}", "(no error)"},
                {"{EventType}", "InstanceStarted"},
                {"{Timestamp}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")},
                {"{Message}", "(sample message)"},
                {"{PID}", "42316"},
                {"{ExitCode}", "0"},
                {"{BuildId}", "22526048"},
                {"{CrashCount}", "3"},
                {"{WindowMinutes}", "10"}
            }
            Dim preview = _templateTextBox.Text
            For Each kvp In sample
                preview = preview.Replace(kvp.Key, kvp.Value)
            Next
            _previewLabel.Text = preview
            _previewLabel.ForeColor = Color.Black
        End Sub

        Private Class EventComboItem
            Public ReadOnly EventType As NotificationEventType
            Public Sub New(t As NotificationEventType)
                Me.EventType = t
            End Sub
            Public Overrides Function ToString() As String
                Return EventType.ToString()
            End Function
        End Class

    End Class

End Namespace