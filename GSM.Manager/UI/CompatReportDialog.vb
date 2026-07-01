Imports System
Imports System.Drawing
Imports System.Windows.Forms
Imports GSM.Manager.Core

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 5l-3 — shows a plugin-compatibility report. Two modes:
    '''   • info (applyMode = False): just a Close button — used by
    '''     Tools > Test plugin compatibility.
    '''   • apply (applyMode = True): adds Cancel + "Apply update", and
    '''     when any plugin is incompatible, an acknowledgement checkbox
    '''     that gates the Apply button (soft-warn, not hard-block).
    '''     <see cref="Proceed"/> is True if the user chose to apply.
    ''' </summary>
    Public Class CompatReportDialog
        Inherits Form

        Private ReadOnly _report As PluginCompatibilityReport
        Private ReadOnly _applyMode As Boolean
        Private _proceed As Boolean = False
        Private _ackCheck As CheckBox
        Private _applyBtn As Button

        ''' <summary>Apply-mode only: True if the user committed to applying.</summary>
        Public ReadOnly Property Proceed As Boolean
            Get
                Return _proceed
            End Get
        End Property

        Public Sub New(report As PluginCompatibilityReport, Optional applyMode As Boolean = False)
            _report = report
            _applyMode = applyMode
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Load, AddressOf OnDialogLoad
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Plugin compatibility"
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MinimizeBox = False
            Me.MaximizeBox = True
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(560, 460)
            Me.MinimumSize = New Size(420, 300)

            Dim root As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 4,
                .Padding = New Padding(14)
            }
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 28))    ' header
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))    ' report
            root.RowStyles.Add(New RowStyle(SizeType.AutoSize))        ' ack checkbox
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 46))    ' buttons

            Dim header As New Label With {
                .Dock = DockStyle.Fill,
                .Font = New Font("Segoe UI", 11, FontStyle.Bold),
                .TextAlign = ContentAlignment.MiddleLeft,
                .Text = BuildHeaderText()
            }
            root.Controls.Add(header, 0, 0)

            _reportBox = New RichTextBox With {
                .Dock = DockStyle.Fill,
                .ReadOnly = True,
                .TabStop = False,
                .HideSelection = True,
                .DetectUrls = False,
                .BorderStyle = BorderStyle.FixedSingle,
                .BackColor = SystemColors.Window,
                .Font = New Font("Segoe UI", 9.5F)
            }
            root.Controls.Add(_reportBox, 0, 1)

            ' Acknowledgement (apply mode + incompatibilities only).
            _ackCheck = New CheckBox With {
                .Dock = DockStyle.Fill,
                .AutoSize = False,
                .Height = 44,
                .ForeColor = Color.FromArgb(150, 40, 40),
                .Text = "I understand the plugin(s) above will stop loading after this update; proceed anyway.",
                .Visible = _applyMode AndAlso _report IsNot Nothing AndAlso _report.AnyIncompatible
            }
            AddHandler _ackCheck.CheckedChanged, Sub(s, e) UpdateApplyEnabled()
            root.Controls.Add(_ackCheck, 0, 2)

            Dim buttons As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.RightToLeft,
                .Padding = New Padding(0, 10, 0, 0)
            }

            If _applyMode Then
                _applyBtn = New Button With {.Text = "Apply update", .Width = 120}
                AddHandler _applyBtn.Click, AddressOf OnApply
                buttons.Controls.Add(_applyBtn)

                Dim cancelBtn As New Button With {.Text = "Cancel", .Width = 90, .DialogResult = DialogResult.Cancel}
                buttons.Controls.Add(cancelBtn)
                Me.CancelButton = cancelBtn
            Else
                Dim closeBtn As New Button With {.Text = "Close", .Width = 90, .DialogResult = DialogResult.OK}
                buttons.Controls.Add(closeBtn)
                Me.CancelButton = closeBtn
                Me.AcceptButton = closeBtn
            End If

            root.Controls.Add(buttons, 0, 3)
            Me.Controls.Add(root)

            UpdateApplyEnabled()
        End Sub

        Private _reportBox As RichTextBox

        Private Function BuildHeaderText() As String
            If _report Is Nothing OrElse _report.PluginCount = 0 Then
                Return "No plugins to check"
            End If
            Dim label = If(String.IsNullOrEmpty(_report.ContractsLabel), "the selected contracts", _report.ContractsLabel)
            Return $"Compatibility against {label}"
        End Function

        Private Sub OnDialogLoad(sender As Object, e As EventArgs)
            RenderReport()
        End Sub

        Private Sub RenderReport()
            _reportBox.Clear()
            If _report Is Nothing OrElse _report.PluginCount = 0 Then
                AppendRun("No plugin source files were found to check." & vbCrLf,
                          New Font("Segoe UI", 9.5F), SystemColors.GrayText)
                Return
            End If

            For Each r In _report.Results
                If r.Compatible Then
                    AppendRun("✓  ", New Font("Segoe UI", 9.5F, FontStyle.Bold), Color.FromArgb(0, 130, 60))
                    AppendRun(r.FileName & " — compatible" & vbCrLf, New Font("Segoe UI", 9.5F), SystemColors.WindowText)
                Else
                    AppendRun("✗  ", New Font("Segoe UI", 9.5F, FontStyle.Bold), Color.FromArgb(170, 40, 40))
                    AppendRun(r.FileName & " — incompatible:" & vbCrLf, New Font("Segoe UI", 9.5F, FontStyle.Bold), Color.FromArgb(170, 40, 40))
                    For Each diag In r.Errors
                        Dim prefix = "        "
                        Dim codePart = If(String.IsNullOrEmpty(diag.ErrorCode), "", diag.ErrorCode & ": ")
                        Dim linePart = If(diag.Line > 0, $" (line {diag.Line})", "")
                        AppendRun(prefix & codePart & diag.Message & linePart & vbCrLf,
                                  New Font("Consolas", 8.75F), Color.FromArgb(120, 50, 50))
                    Next
                End If
            Next

            If _applyMode Then
                AppendRun(vbCrLf, New Font("Segoe UI", 4F), SystemColors.WindowText)
                If _report.AnyIncompatible Then
                    AppendRun("Incompatible plugins won't load after the update — affected instances fall back to plugin-less behaviour (logs still stream; identity resolution and parse rules from those plugins are unavailable) until the plugin is updated." & vbCrLf,
                              New Font("Segoe UI", 8.75F), SystemColors.GrayText)
                Else
                    AppendRun("All plugins compile against the new contracts — they'll load cleanly after the update." & vbCrLf,
                              New Font("Segoe UI", 8.75F), SystemColors.GrayText)
                End If
            End If

            _reportBox.SelectionStart = 0
            _reportBox.SelectionLength = 0
            Try
                _reportBox.ScrollToCaret()
            Catch
            End Try
        End Sub

        Private Sub AppendRun(text As String, font As Font, color As Color)
            If String.IsNullOrEmpty(text) Then Return
            _reportBox.SelectionStart = _reportBox.TextLength
            _reportBox.SelectionLength = 0
            _reportBox.SelectionFont = font
            _reportBox.SelectionColor = color
            _reportBox.AppendText(text)
        End Sub

        Private Sub UpdateApplyEnabled()
            If Not _applyMode OrElse _applyBtn Is Nothing Then Return
            ' Apply is enabled unless there are incompatibilities the user
            ' hasn't acknowledged.
            If _report IsNot Nothing AndAlso _report.AnyIncompatible Then
                _applyBtn.Enabled = _ackCheck IsNot Nothing AndAlso _ackCheck.Checked
            Else
                _applyBtn.Enabled = True
            End If
        End Sub

        Private Sub OnApply(sender As Object, e As EventArgs)
            _proceed = True
            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

    End Class

End Namespace
