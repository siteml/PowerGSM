Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Windows.Forms
Imports GSM.Manager.Core
Imports GSM.Manager.Data

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 5l-3 — Help → Update History. Read-only list of past
    ''' self-update apply attempts (success + failure), newest first.
    ''' </summary>
    Public Class UpdateHistoryDialog
        Inherits Form

        Private ReadOnly _orchestrator As UpdateOrchestrator
        Private _grid As DataGridView
        Private _emptyLabel As Label

        Public Sub New(orchestrator As UpdateOrchestrator)
            _orchestrator = orchestrator
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Load, AddressOf OnDialogLoad
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Update History"
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(700, 420)
            Me.MinimumSize = New Size(500, 280)

            Dim content As New Panel With {.Dock = DockStyle.Fill, .Padding = New Padding(10, 10, 10, 0)}

            _grid = New DataGridView With {
                .Dock = DockStyle.Fill,
                .ReadOnly = True,
                .AllowUserToAddRows = False,
                .AllowUserToDeleteRows = False,
                .AllowUserToResizeRows = False,
                .RowHeadersVisible = False,
                .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                .MultiSelect = False,
                .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                .BackgroundColor = SystemColors.Window,
                .BorderStyle = BorderStyle.FixedSingle,
                .Font = New Font("Segoe UI", 9.0F)
            }
            _grid.Columns.Add(NewCol("When", "When", 140))
            _grid.Columns.Add(NewCol("From", "From", 100))
            _grid.Columns.Add(NewCol("To", "To", 100))
            _grid.Columns.Add(NewCol("Outcome", "Outcome", 80))
            _grid.Columns.Add(NewCol("Detail", "Detail", 220))
            ' Fixed widths for the short columns; Detail takes the fill.
            For Each c As String In New String() {"When", "From", "To", "Outcome"}
                _grid.Columns(c).AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            Next

            _emptyLabel = New Label With {
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleCenter,
                .ForeColor = SystemColors.GrayText,
                .Font = New Font("Segoe UI", 10.0F),
                .Text = "No updates have been applied yet.",
                .Visible = False
            }

            content.Controls.Add(_grid)
            content.Controls.Add(_emptyLabel)

            Dim bottom As New FlowLayoutPanel With {
                .Dock = DockStyle.Bottom,
                .FlowDirection = FlowDirection.RightToLeft,
                .Height = 48,
                .Padding = New Padding(10)
            }
            Dim closeBtn As New Button With {.Text = "Close", .Width = 90, .DialogResult = DialogResult.OK}
            bottom.Controls.Add(closeBtn)
            Me.CancelButton = closeBtn
            Me.AcceptButton = closeBtn

            ' Add the fill content first (index 0 → docks last → fills the
            ' space the bottom strip leaves), then the bottom bar.
            Me.Controls.Add(content)
            Me.Controls.Add(bottom)
        End Sub

        Private Shared Function NewCol(name As String, header As String, width As Integer) As DataGridViewTextBoxColumn
            Return New DataGridViewTextBoxColumn With {
                .Name = name, .HeaderText = header, .Width = width
            }
        End Function

        Private Sub OnDialogLoad(sender As Object, e As EventArgs)
            Dim rows As List(Of UpdateHistoryEntity) =
                If(_orchestrator IsNot Nothing, _orchestrator.GetHistory(200), New List(Of UpdateHistoryEntity)())

            If rows Is Nothing OrElse rows.Count = 0 Then
                _grid.Visible = False
                _emptyLabel.Visible = True
                Return
            End If

            For Each h In rows
                ' EF/SQLite reads DateTime back as Kind=Unspecified, so
                ' stamp it UTC before converting to local for display.
                Dim appliedUtc = DateTime.SpecifyKind(h.AppliedAtUtc, DateTimeKind.Utc)
                Dim whenLocal = appliedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")

                Dim idx = _grid.Rows.Add(
                    whenLocal,
                    If(String.IsNullOrEmpty(h.FromVersion), "—", h.FromVersion),
                    If(String.IsNullOrEmpty(h.ToVersion), "—", h.ToVersion),
                    h.Outcome,
                    If(h.Detail, ""))

                Dim row = _grid.Rows(idx)
                If String.Equals(h.Outcome, "Failed", StringComparison.OrdinalIgnoreCase) Then
                    row.Cells("Outcome").Style.ForeColor = Color.FromArgb(170, 40, 40)
                Else
                    row.Cells("Outcome").Style.ForeColor = Color.FromArgb(0, 130, 60)
                End If
            Next
        End Sub

    End Class

End Namespace
