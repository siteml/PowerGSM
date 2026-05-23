Imports System
Imports System.Drawing
Imports System.Windows.Forms

' ============================================================
'  DetailedErrorDialog — shared resizable error dialog
'
'  Surfaces long or multi-line diagnostic text (engine
'  stdout/stderr, SteamCMD failure hints, etc.) that doesn't
'  fit cleanly into a panel's status label. Used by code paths
'  that need to show full diagnostic content without crowding
'  the host panel.
'
'  Layout matches the in-place ShowGenerationErrorDialog
'  pattern in FileGenerationPanel (and NewInstallationForm's
'  install-error dialog, per that file's comment) so the
'  manager has a consistent look for engine-failure surfaces.
'  A future cleanup could migrate those two callers to this
'  shared helper; not done as part of the version-check work
'  to keep the diff focused on the actual bug fix.
'
'  Visual:
'    - Warning icon + bold headline at the top
'    - "Diagnostic output:" subhead
'    - Multi-line, read-only monospace TextBox with both
'      scrollbars and no word wrap (long lines scroll
'      horizontally rather than wrapping ambiguously, since
'      the diagnostic text often contains paths or stack
'      frames where preserved column alignment matters)
'    - OK button bottom-right; accept and cancel both wired
'      to it so Esc and Enter both close cleanly
'
'  TextBox starts scrolled to the end since most diagnostic
'  output puts the actionable bit (the actual error) at the
'  bottom after a banner of init lines that aren't actionable.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DetailedErrorDialog

        ''' <summary>
        ''' Show a modal error dialog with a resizable monospace
        ''' body TextBox. Owner may be Nothing (the dialog still
        ''' opens, just without a parent window to centre against).
        ''' Returns when the user dismisses the dialog.
        ''' </summary>
        Public Shared Sub Show(owner As IWin32Window,
                                title As String,
                                headline As String,
                                body As String)
            Using dlg As New Form()
                dlg.Text = If(title, "Error")
                dlg.Size = New Size(720, 480)
                dlg.StartPosition = FormStartPosition.CenterParent
                dlg.MinimumSize = New Size(480, 280)
                dlg.FormBorderStyle = FormBorderStyle.Sizable
                dlg.MaximizeBox = True
                dlg.MinimizeBox = False

                Dim icon As New PictureBox() With {
                    .Image = SystemIcons.Warning.ToBitmap(),
                    .SizeMode = PictureBoxSizeMode.AutoSize,
                    .Location = New Point(15, 15)
                }
                dlg.Controls.Add(icon)

                Dim header As New Label() With {
                    .Text = If(String.IsNullOrEmpty(headline),
                                "An error occurred.", headline),
                    .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                    .AutoSize = False,
                    .Size = New Size(610, 36),
                    .Location = New Point(70, 18),
                    .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
                }
                dlg.Controls.Add(header)

                Dim bodyLabel As New Label() With {
                    .Text = "Diagnostic output:",
                    .AutoSize = True,
                    .Location = New Point(15, 60),
                    .Anchor = AnchorStyles.Top Or AnchorStyles.Left
                }
                dlg.Controls.Add(bodyLabel)

                Dim bodyBox As New TextBox() With {
                    .Multiline = True,
                    .ReadOnly = True,
                    .ScrollBars = ScrollBars.Both,
                    .WordWrap = False,
                    .Font = New Font("Consolas", 9.25F),
                    .BackColor = SystemColors.Window,
                    .Text = If(body, ""),
                    .Location = New Point(15, 85),
                    .Size = New Size(675, 320),
                    .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                              AnchorStyles.Right Or AnchorStyles.Bottom
                }
                ' Scroll to the END rather than the start. Error
                ' content typically lands at the bottom of the
                ' captured output (after a banner of init lines that
                ' aren't actionable), so opening at the bottom puts
                ' the diagnostic in front of the user immediately.
                bodyBox.Select(bodyBox.TextLength, 0)
                bodyBox.ScrollToCaret()
                dlg.Controls.Add(bodyBox)

                Dim okButton As New Button() With {
                    .Text = "OK",
                    .Size = New Size(90, 28),
                    .Location = New Point(600, 415),
                    .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right,
                    .DialogResult = DialogResult.OK
                }
                dlg.Controls.Add(okButton)
                dlg.AcceptButton = okButton
                dlg.CancelButton = okButton

                dlg.ShowDialog(owner)
            End Using
        End Sub

    End Class

End Namespace
