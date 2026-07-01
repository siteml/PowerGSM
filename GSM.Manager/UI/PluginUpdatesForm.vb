Imports System
Imports System.Drawing
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager.Core

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 6-4 — Tools → Check for Plugin Updates. Compares every
    ''' installed, version-carrying plugin against the best version
    ''' offered across all enabled sources, and updates one plugin at
    ''' a time via the same stage → consent → install → reload path
    ''' the Plugin Sources dialog uses. Never auto-applies.
    ''' </summary>
    Public Class PluginUpdatesForm
        Inherits Form

        Private ReadOnly _stager As PluginStageService

        Private _grid As ListView
        Private _statusLabel As Label
        Private _updateButton As Button
        Private _recheckButton As Button
        Private _selectAll As CheckBox
        Private _suppressCheckEvents As Boolean

        Public Sub New()
            _stager = ManagerProgram.Services.GetService(Of PluginStageService)()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Load, AddressOf OnDialogLoad
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Plugin Updates"
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(620, 400)
            Me.MinimumSize = New Size(520, 300)

            ' Phase 6 batch UX — checkbox selection with a select-all
            ' toggle; Update acts on CHECKED rows.
            _selectAll = New CheckBox With {
                .Text = "Select all", .Location = New Point(12, 10), .AutoSize = True}
            AddHandler _selectAll.CheckedChanged, AddressOf OnSelectAllChanged
            Me.Controls.Add(_selectAll)

            _grid = New ListView With {
                .View = View.Details, .FullRowSelect = True, .GridLines = True,
                .MultiSelect = True, .HideSelection = False, .CheckBoxes = True,
                .Location = New Point(12, 36), .Size = New Size(580, 266),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right}
            _grid.Columns.Add("Plugin", 180)
            _grid.Columns.Add("Installed", 90)
            _grid.Columns.Add("Latest", 90)
            _grid.Columns.Add("Source", 190)
            AddHandler _grid.ItemChecked, Sub(s, e) OnItemChecked()
            Me.Controls.Add(_grid)

            _statusLabel = New Label With {
                .Location = New Point(12, 318), .Size = New Size(330, 20),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right,
                .AutoEllipsis = True,
                .ForeColor = SystemColors.GrayText, .Text = ""}
            Me.Controls.Add(_statusLabel)

            _recheckButton = New Button With {
                .Text = "Re-check", .Location = New Point(352, 314), .Size = New Size(90, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .Enabled = False}
            AddHandler _recheckButton.Click, Sub(s, e) LoadUpdates(True)
            Me.Controls.Add(_recheckButton)

            _updateButton = New Button With {
                .Text = "Update...", .Location = New Point(448, 314), .Size = New Size(90, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .Enabled = False}
            AddHandler _updateButton.Click, Sub(s, e) OnUpdate()
            Me.Controls.Add(_updateButton)

            Dim closeButton As New Button With {
                .Text = "Close", .Location = New Point(544, 314), .Size = New Size(60, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .DialogResult = DialogResult.OK}
            Me.Controls.Add(closeButton)
            Me.CancelButton = closeButton
        End Sub

        Private Sub OnDialogLoad(sender As Object, e As EventArgs)
            LoadUpdates(False)
        End Sub

        ''' <summary>All CHECKED updates — the batch Update acts on.</summary>
        Private Function CheckedUpdates() As List(Of PluginUpdateInfo)
            Dim updates As New List(Of PluginUpdateInfo)
            For Each item As ListViewItem In _grid.CheckedItems
                Dim u = TryCast(item.Tag, PluginUpdateInfo)
                If u IsNot Nothing AndAlso u.Entry IsNot Nothing Then updates.Add(u)
            Next
            Return updates
        End Function

        Private Sub OnSelectAllChanged(sender As Object, e As EventArgs)
            If _suppressCheckEvents Then Return
            _suppressCheckEvents = True
            Try
                For Each item As ListViewItem In _grid.Items
                    item.Checked = _selectAll.Checked
                Next
            Finally
                _suppressCheckEvents = False
            End Try
            UpdateButtons()
        End Sub

        Private Sub OnItemChecked()
            If _suppressCheckEvents Then Return
            _suppressCheckEvents = True
            Try
                _selectAll.Checked = _grid.Items.Count > 0 AndAlso
                                     _grid.CheckedItems.Count = _grid.Items.Count
            Finally
                _suppressCheckEvents = False
            End Try
            UpdateButtons()
        End Sub

        Private Sub UpdateButtons()
            Dim count = _grid.CheckedItems.Count
            _updateButton.Enabled = count > 0
            _updateButton.Text = If(count > 1, $"Update ({count})...", "Update...")
        End Sub

        Private Async Sub LoadUpdates(forceRefresh As Boolean)
            If _stager Is Nothing Then
                _statusLabel.Text = "Plugin staging service unavailable."
                Return
            End If
            _grid.Items.Clear()
            _suppressCheckEvents = True
            Try
                If _selectAll IsNot Nothing Then _selectAll.Checked = False
            Finally
                _suppressCheckEvents = False
            End Try
            _recheckButton.Enabled = False
            _updateButton.Enabled = False
            _statusLabel.Text = "Checking sources…"

            Dim updates As List(Of PluginUpdateInfo)
            Try
                updates = Await _stager.CheckForUpdatesAsync(forceRefresh)
            Catch ex As Exception
                updates = New List(Of PluginUpdateInfo)()
                _statusLabel.Text = $"Check failed: {ex.Message}"
                _recheckButton.Enabled = True
                Return
            End Try

            For Each u In updates
                Dim item As New ListViewItem(u.PluginId)
                item.SubItems.Add(u.InstalledVersion)
                item.SubItems.Add(u.LatestVersion)
                item.SubItems.Add(If(u.Entry IsNot Nothing, u.Entry.SourceDisplayName, ""))
                item.Tag = u
                _grid.Items.Add(item)
            Next

            _recheckButton.Enabled = True
            _statusLabel.Text = If(updates.Count = 0,
                                   "All plugins are up to date.",
                                   If(updates.Count = 1, "1 update available.", $"{updates.Count} updates available."))
        End Sub

        ''' <summary>
        ''' Update every selected plugin: stage the newer versions, show
        ''' ONE combined consent (self-collision warnings folded into the
        ''' natural "update X → Y" framing; other warnings surfaced
        ''' explicitly), install the lot, reload ONCE, refresh the list.
        ''' </summary>
        Private Async Sub OnUpdate()
            Dim selected = CheckedUpdates()
            If selected.Count = 0 Then Return

            _updateButton.Enabled = False

            ' 1) Stage all.
            Dim stagedPairs As New List(Of (Update As PluginUpdateInfo, Staged As StagedPlugin))
            Dim failures As New List(Of String)
            Dim index = 0
            For Each u In selected
                index += 1
                _statusLabel.Text = $"Downloading {index}/{selected.Count}: {u.PluginId} {u.LatestVersion}…"
                Dim staged As PluginStageResult
                Try
                    staged = Await _stager.StageAsync(u.Entry)
                Catch ex As Exception
                    staged = New PluginStageResult With {.Ok = False, .ErrorMessage = ex.Message}
                End Try
                If staged IsNot Nothing AndAlso staged.Ok Then
                    stagedPairs.Add((u, staged.Staged))
                Else
                    failures.Add($"{u.PluginId}: {If(staged?.ErrorMessage, "download failed")}")
                End If
            Next

            UpdateButtons()
            If stagedPairs.Count = 0 Then
                _statusLabel.Text = "Update failed."
                MessageBox.Show(Me, "No updates could be staged:" & Environment.NewLine & Environment.NewLine &
                                "  • " & String.Join(Environment.NewLine & "  • ", failures),
                                "Update Plugins", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' 2) One combined consent. Updating the same plugin id always
            ' trips the collision warning ("already installed... will
            ' replace it") — expected here, folded into the → framing;
            ' any OTHER warnings are surfaced explicitly per plugin.
            Dim anyWarnings = False
            Dim lines As New List(Of String)
            For Each pair In stagedPairs
                lines.Add($"• {pair.Update.PluginId} {pair.Update.InstalledVersion} → {pair.Update.LatestVersion}")
                ' Phase 7-3 — declared capabilities, shown for consent
                ' (an update can ADD capabilities vs the installed
                ' version, so they're worth re-reading here).
                If pair.Staged.Capabilities IsNot Nothing AndAlso pair.Staged.Capabilities.Count > 0 Then
                    lines.Add($"      Requires: {String.Join(", ", pair.Staged.Capabilities)}")
                    If pair.Staged.Capabilities.Contains("web-capture") Then
                        lines.Add("      This plugin may ask you to log into a website in an embedded browser and will receive the resulting session cookies.")
                    End If
                End If
                ' Phase 7-3b — static source audit notes (advisory).
                If pair.Staged.AuditNotes IsNot Nothing AndAlso pair.Staged.AuditNotes.Count > 0 Then
                    For Each note In pair.Staged.AuditNotes
                        lines.Add($"      ⓘ {note}")
                    Next
                End If
                Dim otherWarnings = pair.Staged.Warnings.FindAll(
                    Function(w) Not w.Contains("is already installed", StringComparison.OrdinalIgnoreCase))
                If otherWarnings.Count > 0 Then
                    anyWarnings = True
                    For Each w In otherWarnings
                        lines.Add($"      ⚠ {w}")
                    Next
                End If
            Next

            Dim prompt = If(stagedPairs.Count = 1, "Apply this update?", $"Apply {stagedPairs.Count} updates?") &
                         Environment.NewLine & Environment.NewLine &
                         String.Join(Environment.NewLine, lines)
            If failures.Count > 0 Then
                prompt &= Environment.NewLine & Environment.NewLine &
                          $"({failures.Count} could not be staged and will be skipped.)"
            End If
            prompt &= Environment.NewLine & Environment.NewLine &
                      "This replaces the installed plugin file(s) and reloads plugins once."

            If MessageBox.Show(Me, prompt, "Update Plugins", MessageBoxButtons.YesNo,
                               If(anyWarnings, MessageBoxIcon.Warning, MessageBoxIcon.Question)) <> DialogResult.Yes Then
                _statusLabel.Text = $"Staged {stagedPairs.Count} update(s) — not installed."
                Return
            End If

            ' 3) Install all, reload once.
            Dim updatedCount = 0
            For Each pair In stagedPairs
                Dim installResult = _stager.InstallStaged(pair.Staged.PluginId)
                If installResult IsNot Nothing AndAlso installResult.Ok Then
                    updatedCount += 1
                Else
                    failures.Add($"{pair.Staged.PluginId}: {If(installResult?.ErrorMessage, "install failed")}")
                End If
            Next

            If updatedCount > 0 Then
                Try
                    Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                    Dim orphanDetector = ManagerProgram.Services.GetService(Of PluginOrphanDetector)()
                    If registry IsNot Nothing Then registry.ReloadAll(orphanDetector)
                Catch ex As Exception
                    _statusLabel.Text = $"Installed, but reload failed: {ex.Message}"
                    Return
                End Try
            End If

            _statusLabel.Text = $"Updated {updatedCount} plugin(s)."
            If failures.Count > 0 Then
                MessageBox.Show(Me, "Some updates had problems:" & Environment.NewLine & Environment.NewLine &
                                "  • " & String.Join(Environment.NewLine & "  • ", failures),
                                "Update Plugins", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If

            ' Refresh the list — the updated plugins should drop out.
            LoadUpdates(False)
        End Sub

    End Class

End Namespace
