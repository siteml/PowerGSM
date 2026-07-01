Imports System
Imports System.Drawing
Imports System.Threading
Imports System.Windows.Forms
Imports GSM.Manager.Core

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 5l-2 — modal progress dialog for downloading + staging an
    ''' update. Runs UpdateOrchestrator.StageAsync, shows download
    ''' progress (and the verify/extract phases as indeterminate), and
    ''' offers Cancel. The outcome is exposed via <see cref="Result"/>.
    ''' </summary>
    Public Class UpdateProgressDialog
        Inherits Form

        Private ReadOnly _orchestrator As UpdateOrchestrator
        Private ReadOnly _status As UpdateStatus
        Private ReadOnly _cts As New CancellationTokenSource()
        Private _bar As ProgressBar
        Private _phaseLabel As Label
        Private _detailLabel As Label
        Private _cancelBtn As Button
        Private _finished As Boolean = False

        ''' <summary>Set when staging completes (success, cancel, or error).</summary>
        Public Property Result As StageResult

        Public Sub New(orchestrator As UpdateOrchestrator, status As UpdateStatus)
            _orchestrator = orchestrator
            _status = status
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Shown, AddressOf OnDialogShown
            AddHandler Me.FormClosing, AddressOf OnDialogClosing
        End Sub

        Private Sub InitializeControls()
            Dim ver = If(_status IsNot Nothing, _status.LatestVersion, "")
            Me.Text = If(String.IsNullOrEmpty(ver), "Downloading update", $"Downloading update {ver}")
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.ControlBox = False            ' force use of the Cancel button
            Me.StartPosition = FormStartPosition.CenterParent
            Me.ClientSize = New Size(420, 140)

            _phaseLabel = New Label With {
                .Text = "Preparing…",
                .Location = New Point(16, 16),
                .AutoSize = True,
                .Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
            }
            Me.Controls.Add(_phaseLabel)

            _bar = New ProgressBar With {
                .Location = New Point(16, 44),
                .Size = New Size(388, 22),
                .Style = ProgressBarStyle.Marquee,
                .MarqueeAnimationSpeed = 30
            }
            Me.Controls.Add(_bar)

            _detailLabel = New Label With {
                .Text = "",
                .Location = New Point(16, 72),
                .AutoSize = True,
                .ForeColor = SystemColors.GrayText
            }
            Me.Controls.Add(_detailLabel)

            _cancelBtn = New Button With {
                .Text = "Cancel",
                .Size = New Size(90, 28),
                .Location = New Point(314, 100)
            }
            AddHandler _cancelBtn.Click, AddressOf OnCancelClicked
            Me.Controls.Add(_cancelBtn)
            Me.CancelButton = _cancelBtn
        End Sub

        ''' <summary>
        ''' Progress(Of T) is constructed on the UI thread, so its
        ''' callback marshals back to the UI thread automatically — the
        ''' orchestrator can Report from its background download loop.
        ''' </summary>
        Private Async Sub OnDialogShown(sender As Object, e As EventArgs)
            Dim progress As New Progress(Of StageProgress)(AddressOf OnProgress)
            Dim r As StageResult
            Try
                r = Await _orchestrator.StageAsync(_status, progress, _cts.Token)
            Catch ex As Exception
                r = New StageResult With {.ErrorMessage = ex.Message}
            End Try
            Result = r
            _finished = True
            Me.DialogResult = If(r IsNot Nothing AndAlso r.Success, DialogResult.OK, DialogResult.Cancel)
            Me.Close()
        End Sub

        Private Sub OnProgress(p As StageProgress)
            Dim phase = If(String.IsNullOrEmpty(p.Phase), "Working", p.Phase)
            _phaseLabel.Text = phase & "…"

            If String.Equals(phase, "Downloading", StringComparison.Ordinal) AndAlso p.HasTotal Then
                _bar.Style = ProgressBarStyle.Continuous
                _bar.Value = Math.Max(0, Math.Min(100, p.Percent))
                _detailLabel.Text = $"{p.Percent}%   ({FormatMB(p.BytesReceived)} of {FormatMB(p.TotalBytes)})"
            ElseIf String.Equals(phase, "Downloading", StringComparison.Ordinal) Then
                _bar.Style = ProgressBarStyle.Marquee
                _detailLabel.Text = FormatMB(p.BytesReceived) & " downloaded"
            Else
                ' Verifying / Extracting — no byte count, show indeterminate.
                _bar.Style = ProgressBarStyle.Marquee
                _detailLabel.Text = ""
            End If
        End Sub

        Private Sub OnCancelClicked(sender As Object, e As EventArgs)
            _cancelBtn.Enabled = False
            _phaseLabel.Text = "Canceling…"
            _cts.Cancel()
        End Sub

        Private Sub OnDialogClosing(sender As Object, e As FormClosingEventArgs)
            ' Don't let the window close out from under a running stage
            ' except via our own completion path — route Esc/Alt-F4 to
            ' a cancel request instead.
            If Not _finished Then
                e.Cancel = True
                _cancelBtn.Enabled = False
                _phaseLabel.Text = "Canceling…"
                _cts.Cancel()
            End If
        End Sub

        Private Shared Function FormatMB(bytes As Long) As String
            If bytes < 0 Then Return "?"
            Return (bytes / (1024.0 * 1024.0)).ToString("0.0") & " MB"
        End Function

    End Class

End Namespace
