Imports System
Imports System.Diagnostics
Imports System.Drawing
Imports System.Windows.Forms
Imports GSM.Manager.Core
Imports Microsoft.Extensions.DependencyInjection
Imports TheArtOfDev.HtmlRenderer.WinForms
Imports TheArtOfDev.HtmlRenderer.Core.Entities

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 5l-1 — passive update dialog. Shows the latest release
    ''' (or "up to date"), the release notes, and lets the user open
    ''' the release on GitHub or skip the version. One-click download
    ''' and apply arrive with Phase 5l-2 / 5l-3; until then "View on
    ''' GitHub" is the action so the user can grab the zip manually.
    ''' </summary>
    Public Class UpdateDialog
        Inherits Form

        Private ReadOnly _status As UpdateStatus
        Private ReadOnly _installWritable As Boolean = True
        Private _skipRequested As Boolean = False
        Private _notesHost As Panel
        Private _buttons As FlowLayoutPanel
        Private _closeBtn As Button
        Private _hint As Label
        Private ReadOnly _orchestrator As UpdateOrchestrator
        Private _staged As StagedState
        Private _applyRequested As Boolean

        ''' <summary>True if the user clicked "Skip this version".</summary>
        Public ReadOnly Property SkipRequested As Boolean
            Get
                Return _skipRequested
            End Get
        End Property

        ''' <summary>True if the user committed to applying the staged update.</summary>
        Public ReadOnly Property ApplyRequested As Boolean
            Get
                Return _applyRequested
            End Get
        End Property

        Public Sub New(status As UpdateStatus, Optional installWritable As Boolean = True)
            _status = status
            _installWritable = installWritable
            Try
                _orchestrator = ManagerProgram.Services.GetService(Of UpdateOrchestrator)()
            Catch
            End Try
            _staged = If(_orchestrator IsNot Nothing, _orchestrator.GetStagedState(), New StagedState())
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Load, AddressOf OnDialogLoad
        End Sub

        Private Sub InitializeControls()
            Dim available = _status IsNot Nothing AndAlso _status.IsUpdateAvailable

            Me.Text = If(available, "Update available", "Check for updates")
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(560, 520)

            Dim root As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 5,
                .Padding = New Padding(14)
            }
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 34))    ' header
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 24))    ' sub-line
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))    ' release notes
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 40))    ' hint
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))    ' buttons

            ' Header
            Dim header As New Label With {
                .Dock = DockStyle.Fill,
                .Font = New Font("Segoe UI", 13, FontStyle.Bold),
                .TextAlign = ContentAlignment.MiddleLeft
            }
            If available Then
                Dim tag = If(String.IsNullOrEmpty(_status.LatestTag), "v" & _status.LatestVersion, _status.LatestTag)
                header.Text = $"Update available: {tag}"
                If _status.IsPrerelease Then header.Text &= "  (pre-release)"
            Else
                header.Text = "You're up to date"
            End If
            root.Controls.Add(header, 0, 0)

            ' Sub-line: current → latest, or last error
            Dim subLabel As New Label With {
                .Dock = DockStyle.Fill,
                .ForeColor = SystemColors.GrayText,
                .TextAlign = ContentAlignment.MiddleLeft
            }
            If _status Is Nothing Then
                subLabel.Text = ""
            ElseIf Not _status.CheckSucceeded Then
                subLabel.Text = "Last check failed: " & _status.ErrorMessage
                subLabel.ForeColor = Color.FromArgb(160, 60, 60)
            ElseIf available Then
                subLabel.Text = $"You have v{_status.CurrentVersion}. Latest is {_status.LatestVersion}."
            Else
                subLabel.Text = $"v{_status.CurrentVersion} is the latest version."
            End If
            root.Controls.Add(subLabel, 0, 1)

            ' Release notes host. The actual notes control (an
            ' HtmlRenderer HtmlPanel, or a RichTextBox fallback) is
            ' built in OnLoad once a window handle exists — HtmlPanel
            ' lays out on assignment and RichTextBox formatting needs
            ' the handle. A bordered panel frames the slot even before
            ' content arrives.
            _notesHost = New Panel With {
                .Dock = DockStyle.Fill,
                .BorderStyle = BorderStyle.FixedSingle,
                .BackColor = SystemColors.Window
            }
            root.Controls.Add(_notesHost, 0, 2)

            ' Hint line — text/colour depend on state, set by RebuildButtons.
            _hint = New Label With {
                .Dock = DockStyle.Fill,
                .ForeColor = SystemColors.GrayText,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Font = New Font("Segoe UI", 8.5F)
            }
            root.Controls.Add(_hint, 0, 3)

            ' Buttons (right-to-left flow: Close is rightmost). Built by
            ' RebuildButtons so the row can be refreshed after a download.
            _buttons = New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.RightToLeft,
                .Padding = New Padding(0, 10, 0, 0)
            }
            root.Controls.Add(_buttons, 0, 4)

            RebuildButtons()

            Me.Controls.Add(root)
        End Sub

        ''' <summary>
        ''' (Re)build the button row + hint for the current state:
        ''' already-staged (Apply [pending 5l-3] / Discard), available +
        ''' writable (Download / Skip), or the read-only / up-to-date
        ''' variants. Always offers Close and, when there's a release
        ''' URL, View on GitHub.
        ''' </summary>
        Private Sub RebuildButtons()
            _buttons.Controls.Clear()

            Dim available = _status IsNot Nothing AndAlso _status.IsUpdateAvailable
            Dim stagedForLatest = _staged IsNot Nothing AndAlso _staged.HasStaged AndAlso
                                  _status IsNot Nothing AndAlso
                                  String.Equals(_staged.Version, _status.LatestVersion, StringComparison.OrdinalIgnoreCase)

            _closeBtn = New Button With {.Text = "Close", .Width = 90, .DialogResult = DialogResult.Cancel}
            _buttons.Controls.Add(_closeBtn)
            Me.CancelButton = _closeBtn
            Me.AcceptButton = _closeBtn

            Dim hintText As String = ""
            Dim hintColor As Color = SystemColors.GrayText

            If stagedForLatest Then
                Me.Text = "Update ready"
                Dim applyBtn As New Button With {.Text = "Apply update", .Width = 110}
                AddHandler applyBtn.Click, AddressOf OnApply
                _buttons.Controls.Add(applyBtn)

                Dim discardBtn As New Button With {.Text = "Discard download", .Width = 130}
                AddHandler discardBtn.Click, AddressOf OnDiscard
                _buttons.Controls.Add(discardBtn)

                hintText = $"Update {_staged.Version} is downloaded and verified. Applying it closes PowerGSM, swaps the program files, and restarts."
            ElseIf available AndAlso _installWritable Then
                Dim downloadBtn As New Button With {.Text = "Download update", .Width = 130}
                AddHandler downloadBtn.Click, AddressOf OnDownload
                _buttons.Controls.Add(downloadBtn)

                Dim skipBtn As New Button With {.Text = "Skip this version", .Width = 120}
                AddHandler skipBtn.Click, AddressOf OnSkip
                _buttons.Controls.Add(skipBtn)

                hintText = "Download stages the update; applying it arrives in the next update. You can also View on GitHub."
            ElseIf available Then
                Dim skipBtn As New Button With {.Text = "Skip this version", .Width = 120}
                AddHandler skipBtn.Click, AddressOf OnSkip
                _buttons.Controls.Add(skipBtn)

                hintText = "This install location isn't writable, so automatic updates can't run here. Move PowerGSM to a writable folder (e.g. %USERPROFILE%\PowerGSM), or use View on GitHub to update manually."
                hintColor = Color.FromArgb(160, 90, 0)
            End If

            If _status IsNot Nothing AndAlso Not String.IsNullOrEmpty(_status.ReleaseUrl) Then
                Dim viewBtn As New Button With {.Text = "View on GitHub", .Width = 120}
                AddHandler viewBtn.Click, AddressOf OnViewOnGitHub
                _buttons.Controls.Add(viewBtn)
            End If

            _hint.Text = hintText
            _hint.ForeColor = hintColor
        End Sub

        Private Sub OnSkip(sender As Object, e As EventArgs)
            _skipRequested = True
            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Sub OnDownload(sender As Object, e As EventArgs)
            If _orchestrator Is Nothing Then
                MessageBox.Show(Me, "The update service isn't available.", "Download update",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            Using dlg As New UpdateProgressDialog(_orchestrator, _status)
                dlg.ShowDialog(Me)
                Dim r = dlg.Result
                If r IsNot Nothing AndAlso r.Success Then
                    _staged = _orchestrator.GetStagedState()
                    RebuildButtons()
                    MessageBox.Show(Me,
                        $"Update {r.Version} was downloaded and verified. It's staged and ready; applying it arrives in the next update.",
                        "Download complete", MessageBoxButtons.OK, MessageBoxIcon.Information)
                ElseIf r IsNot Nothing AndAlso Not r.Canceled AndAlso Not String.IsNullOrEmpty(r.ErrorMessage) Then
                    MessageBox.Show(Me, "Download failed: " & r.ErrorMessage, "Download update",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning)
                End If
            End Using
        End Sub

        Private Sub OnDiscard(sender As Object, e As EventArgs)
            If _orchestrator Is Nothing Then Return
            _orchestrator.DiscardStaged()
            _staged = _orchestrator.GetStagedState()
            Me.Text = If(_status IsNot Nothing AndAlso _status.IsUpdateAvailable, "Update available", "Check for updates")
            RebuildButtons()
        End Sub

        ''' <summary>
        ''' Apply the staged update: downgrade guard, then a dry-run
        ''' plugin-compat check against the STAGED contracts (soft-warn
        ''' with acknowledgement), then prepare apply.cmd and close so
        ''' ManagerProgram spawns it on exit.
        ''' </summary>
        Private Sub OnApply(sender As Object, e As EventArgs)
            If _orchestrator Is Nothing OrElse _staged Is Nothing OrElse Not _staged.HasStaged Then Return

            ' Downgrade guard (belt-and-suspenders — the checker never
            ' offers a downgrade, but a staged folder could be stale).
            Dim cmp = _orchestrator.StagedVersusRunning(_staged.Version)
            If cmp.HasValue AndAlso cmp.Value < 0 Then
                MessageBox.Show(Me,
                    $"The staged update ({_staged.Version}) is older than the version you're running. Applying it would downgrade PowerGSM and could leave the database on a newer schema than the older build understands, so it was blocked.",
                    "Apply update", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            ElseIf cmp.HasValue AndAlso cmp.Value = 0 Then
                MessageBox.Show(Me, $"{_staged.Version} is already the running version — nothing to apply.",
                                "Apply update", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            ' Pre-flight warnings (round 3): in-flight automation and
            ' running instances. Both are informed-consent only — the
            ' game servers run on the node and a Manager restart never
            ' touches them.
            Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
            If engine IsNot Nothing Then
                Dim runningRules = engine.GetRunningRuleNames()
                If runningRules IsNot Nothing AndAlso runningRules.Count > 0 Then
                    Dim ruleList = "    • " & String.Join(Environment.NewLine & "    • ", runningRules)
                    Dim msg = "An automation rule is currently running:" & Environment.NewLine & Environment.NewLine &
                              ruleList & Environment.NewLine & Environment.NewLine &
                              "Applying the update now interrupts it, and it will not resume after the restart. You can let it finish and apply later instead." & Environment.NewLine & Environment.NewLine &
                              "Continue with the update?"
                    If MessageBox.Show(Me, msg, "Apply update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
                End If
            End If

            Dim instMgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If instMgr IsNot Nothing Then
                Dim runningCount = instMgr.GetRunningInstanceCount()
                If runningCount > 0 Then
                    Dim noun = If(runningCount = 1, "instance is", "instances are")
                    Dim msg = $"{runningCount} {noun} currently running." & Environment.NewLine & Environment.NewLine &
                              "The game servers run on the node, not the Manager, so they keep running through the update — only the Manager's live log streams disconnect briefly. When the new Manager restarts it reconnects and catches up on everything that happened while it was down (joins, leaves, server state, chat), so nothing is lost." & Environment.NewLine & Environment.NewLine &
                              "Continue with the update?"
                    If MessageBox.Show(Me, msg, "Apply update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) <> DialogResult.Yes Then Return
                End If
            End If

            ' Pre-flight: compile every plugin against the staged
            ' contracts. Incompatibilities are a soft warning gated by
            ' the acknowledgement checkbox in CompatReportDialog.
            Dim proceed As Boolean
            Dim checker = ManagerProgram.Services.GetService(Of PluginCompatibilityChecker)()
            If checker IsNot Nothing Then
                Dim contractsPath = IO.Path.Combine(_staged.ExtractedPath, "GSM.Contracts.dll")
                Dim report As PluginCompatibilityReport
                Try
                    Me.UseWaitCursor = True
                    report = checker.Check(contractsPath, $"{_staged.Version} (staged)")
                Finally
                    Me.UseWaitCursor = False
                End Try
                Using dlg As New CompatReportDialog(report, applyMode:=True)
                    dlg.ShowDialog(Me)
                    proceed = dlg.Proceed
                End Using
            Else
                proceed = MessageBox.Show(Me,
                    "Apply the staged update now? PowerGSM will close and restart.",
                    "Apply update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) = DialogResult.Yes
            End If
            If Not proceed Then Return

            Dim prep = _orchestrator.RequestApply(_staged)
            If Not prep.Ok Then
                MessageBox.Show(Me, "Couldn't prepare the update: " & prep.ErrorMessage,
                                "Apply update", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            _applyRequested = True
            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        ''' <summary>
        ''' Build the notes control once the window handle exists.
        ''' Prefers an HtmlRenderer HtmlPanel (GitHub-style rendering);
        ''' if anything about HtmlRenderer fails to load or render,
        ''' falls back to the dependency-free RichTextBox renderer, and
        ''' finally to plain text — so the dialog can never break on a
        ''' notes-rendering problem.
        ''' </summary>
        Private Sub OnDialogLoad(sender As Object, e As EventArgs)
            Dim hasBody = _status IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_status.ReleaseBody)
            If Not hasBody Then
                AddRichTextNotes(Nothing)   ' shows the friendly fallback text
                Return
            End If

            Try
                AddHtmlNotes(_status.ReleaseBody)
            Catch
                ' HtmlRenderer unavailable or threw — degrade gracefully.
                Try
                    _notesHost.Controls.Clear()
                Catch
                End Try
                AddRichTextNotes(_status.ReleaseBody)
            End Try
        End Sub

        Private Sub AddHtmlNotes(body As String)
            Dim panel As New HtmlPanel With {
                .Dock = DockStyle.Fill,
                .BackColor = SystemColors.Window,
                .BaseStylesheet = NotesStylesheet(),
                .Font = New Font("Segoe UI", 9.0F)
            }
            AddHandler panel.LinkClicked, AddressOf OnHtmlLinkClicked
            panel.Text = "<body>" & MarkdownToHtml.Convert(body) & "</body>"
            _notesHost.Controls.Add(panel)
        End Sub

        Private Sub AddRichTextNotes(body As String)
            Dim rtb As New RichTextBox With {
                .Dock = DockStyle.Fill,
                .ReadOnly = True,
                .TabStop = False,
                .HideSelection = True,
                .DetectUrls = False,
                .BorderStyle = BorderStyle.None,
                .BackColor = SystemColors.Window,
                .ScrollBars = RichTextBoxScrollBars.Vertical,
                .Font = New Font("Segoe UI", 9.5F)
            }
            _notesHost.Controls.Add(rtb)
            If String.IsNullOrWhiteSpace(body) Then
                rtb.Text = BuildNotesText()
                Return
            End If
            Try
                MarkdownRenderer.Render(rtb, body)
            Catch
                rtb.Text = body
            End Try
        End Sub

        Private Sub OnHtmlLinkClicked(sender As Object, e As HtmlLinkClickedEventArgs)
            e.Handled = True
            Try
                If Not String.IsNullOrEmpty(e.Link) Then
                    Process.Start(New ProcessStartInfo(e.Link) With {.UseShellExecute = True})
                End If
            Catch
            End Try
        End Sub

        ''' <summary>GitHub-ish base stylesheet for the notes HtmlPanel.</summary>
        Private Shared Function NotesStylesheet() As String
            Return "body { font-size: 9pt; color: #1f2328; margin: 0; padding: 6px; }" &
                   "h1 { font-size: 14pt; font-weight: bold; margin: 10px 0 4px 0; }" &
                   "h2 { font-size: 12pt; font-weight: bold; margin: 10px 0 4px 0; }" &
                   "h3 { font-size: 11pt; font-weight: bold; margin: 8px 0 3px 0; }" &
                   "h4 { font-size: 10pt; font-weight: bold; margin: 8px 0 3px 0; }" &
                   "h5 { font-size: 9pt; font-weight: bold; margin: 6px 0 3px 0; }" &
                   "h6 { font-size: 9pt; font-weight: bold; margin: 6px 0 3px 0; }" &
                   "p { margin: 4px 0; }" &
                   "ul { margin: 4px 0; padding-left: 22px; }" &
                   "ol { margin: 4px 0; padding-left: 26px; }" &
                   "li { margin: 2px 0; }" &
                   "code { font-family: Consolas; background-color: #eff1f3; color: #b3306a; padding: 0 3px; }" &
                   "pre { font-family: Consolas; background-color: #f6f8fa; border: 1px solid #e2e4e8; padding: 6px; margin: 4px 0; }" &
                   "a { color: #0a66c2; text-decoration: none; }" &
                   "hr { border: 0; border-top: 1px solid #d0d7de; margin: 8px 0; }" &
                   "blockquote { margin: 4px 0; padding-left: 10px; border-left: 3px solid #d0d7de; color: #57606a; }" &
                   "strong { font-weight: bold; }" &
                   "em { font-style: italic; }"
        End Function

        ''' <summary>
        ''' Release body as plain text, line endings normalised to
        ''' CRLF so the multiline TextBox renders them. Falls back to
        ''' a friendly message when there's no body.
        ''' </summary>
        Private Function BuildNotesText() As String
            If _status IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_status.ReleaseBody) Then
                Return _status.ReleaseBody.Replace(vbCrLf, vbLf).Replace(vbLf, vbCrLf)
            End If
            If _status IsNot Nothing AndAlso _status.IsUpdateAvailable Then
                Return "(No release notes were provided for this release.)"
            End If
            Return "No newer version is available right now."
        End Function

        Private Sub OnViewOnGitHub(sender As Object, e As EventArgs)
            Try
                If _status IsNot Nothing AndAlso Not String.IsNullOrEmpty(_status.ReleaseUrl) Then
                    Process.Start(New ProcessStartInfo(_status.ReleaseUrl) With {.UseShellExecute = True})
                End If
            Catch ex As Exception
                MessageBox.Show(Me, "Couldn't open the browser: " & ex.Message, "View on GitHub",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End Try
        End Sub

    End Class

End Namespace
