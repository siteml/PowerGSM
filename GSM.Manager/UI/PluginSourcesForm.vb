Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager.Core
Imports GSM.Manager.Data

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 6-2 — Tools → Plugin Sources. Top: CRUD over the
    ''' GitHub sources the Manager browses for plugins (the official
    ''' source is un-deletable, only toggle-able). Bottom: the live
    ''' catalog of the selected source, fetched via
    ''' PluginCatalogService (contents-API list + raw header parse).
    ''' Browse-only for now — Install arrives in 6-4.
    ''' </summary>
    Public Class PluginSourcesForm
        Inherits Form

        Private ReadOnly _catalog As PluginCatalogService

        Private _sourcesView As ListView
        Private _catalogView As ListView
        Private _statusLabel As Label
        Private _addButton As Button
        Private _editButton As Button
        Private _removeButton As Button
        Private _toggleButton As Button
        Private _refreshButton As Button
        Private _downloadButton As Button
        Private _catalogSelectAll As CheckBox
        Private _suppressCheckEvents As Boolean

        Public Sub New()
            _catalog = ManagerProgram.Services.GetService(Of PluginCatalogService)()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Load, AddressOf OnDialogLoad
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Plugin Sources"
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(760, 580)
            Me.MinimumSize = New Size(620, 440)

            Dim sourcesLabel As New Label With {
                .Text = "Sources", .Location = New Point(12, 10), .AutoSize = True,
                .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)}
            Me.Controls.Add(sourcesLabel)

            _sourcesView = New ListView With {
                .View = View.Details, .FullRowSelect = True, .GridLines = True,
                .MultiSelect = False, .HideSelection = False,
                .Location = New Point(12, 32), .Size = New Size(590, 150),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right}
            _sourcesView.Columns.Add("Name", 180)
            _sourcesView.Columns.Add("Repo", 190)
            _sourcesView.Columns.Add("Path", 110)
            _sourcesView.Columns.Add("Branch", 70)
            _sourcesView.Columns.Add("Official", 60)
            _sourcesView.Columns.Add("Enabled", 60)
            AddHandler _sourcesView.SelectedIndexChanged, Sub(s, e) OnSourceSelectionChanged()
            Me.Controls.Add(_sourcesView)

            _addButton = New Button With {
                .Text = "Add...", .Location = New Point(614, 32), .Size = New Size(120, 28),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
            AddHandler _addButton.Click, Sub(s, e) OnAdd()
            Me.Controls.Add(_addButton)

            _editButton = New Button With {
                .Text = "Edit...", .Location = New Point(614, 66), .Size = New Size(120, 28),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
            AddHandler _editButton.Click, Sub(s, e) OnEdit()
            Me.Controls.Add(_editButton)

            _removeButton = New Button With {
                .Text = "Remove", .Location = New Point(614, 100), .Size = New Size(120, 28),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
            AddHandler _removeButton.Click, Sub(s, e) OnRemove()
            Me.Controls.Add(_removeButton)

            _toggleButton = New Button With {
                .Text = "Enable", .Location = New Point(614, 134), .Size = New Size(120, 28),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
            AddHandler _toggleButton.Click, Sub(s, e) OnToggleEnabled()
            Me.Controls.Add(_toggleButton)

            Dim catalogLabel As New Label With {
                .Text = "Catalog", .Location = New Point(12, 192), .AutoSize = True,
                .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)}
            Me.Controls.Add(catalogLabel)

            ' Phase 6 batch UX — checkbox selection with a select-all
            ' toggle; Install acts on CHECKED entries.
            _catalogSelectAll = New CheckBox With {
                .Text = "Select all", .Location = New Point(80, 190), .AutoSize = True}
            AddHandler _catalogSelectAll.CheckedChanged, AddressOf OnCatalogSelectAllChanged
            Me.Controls.Add(_catalogSelectAll)

            _refreshButton = New Button With {
                .Text = "Refresh", .Location = New Point(614, 188), .Size = New Size(120, 28),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
            AddHandler _refreshButton.Click, Sub(s, e) OnRefreshCatalog()
            Me.Controls.Add(_refreshButton)

            _catalogView = New ListView With {
                .View = View.Details, .FullRowSelect = True, .GridLines = True,
                .MultiSelect = True, .HideSelection = False, .CheckBoxes = True,
                .Location = New Point(12, 216), .Size = New Size(722, 280),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right}
            _catalogView.Columns.Add("Plugin", 200)
            _catalogView.Columns.Add("Id", 140)
            _catalogView.Columns.Add("Version", 70)
            _catalogView.Columns.Add("Author", 110)
            _catalogView.Columns.Add("Origin", 90)
            _catalogView.Columns.Add("File", 100)
            AddHandler _catalogView.ItemChecked, Sub(s, e) OnCatalogItemChecked()
            Me.Controls.Add(_catalogView)

            _statusLabel = New Label With {
                .Location = New Point(12, 506), .Size = New Size(498, 20),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right,
                .AutoEllipsis = True,
                .ForeColor = SystemColors.GrayText, .Text = ""}
            Me.Controls.Add(_statusLabel)

            ' Phase 6-3/6-4 — stage (download + validate) then offer to
            ' install the selected catalog entry.
            _downloadButton = New Button With {
                .Text = "Install...", .Location = New Point(524, 506), .Size = New Size(110, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .Enabled = False}
            AddHandler _downloadButton.Click, Sub(s, e) OnDownload()
            Me.Controls.Add(_downloadButton)

            Dim closeButton As New Button With {
                .Text = "Close", .Location = New Point(644, 506), .Size = New Size(90, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .DialogResult = DialogResult.OK}
            Me.Controls.Add(closeButton)
            Me.CancelButton = closeButton
            Me.AcceptButton = closeButton
        End Sub

        Private Sub OnDialogLoad(sender As Object, e As EventArgs)
            RefreshSources()
        End Sub

        Private Sub RefreshSources()
            _sourcesView.Items.Clear()
            If _catalog Is Nothing Then
                _statusLabel.Text = "Plugin catalog service unavailable."
                Return
            End If
            For Each src In _catalog.GetSources()
                Dim item As New ListViewItem(src.DisplayName)
                item.SubItems.Add($"{src.Owner}/{src.Repo}")
                item.SubItems.Add(If(String.IsNullOrEmpty(src.RepoPath), "(root)", src.RepoPath))
                item.SubItems.Add(src.Branch)
                item.SubItems.Add(If(src.IsOfficial, "Yes", ""))
                item.SubItems.Add(If(src.IsEnabled, "Yes", "No"))
                item.Tag = src
                _sourcesView.Items.Add(item)
            Next
            UpdateButtons()
        End Sub

        Private Function SelectedSource() As PluginSourceEntity
            If _sourcesView.SelectedItems.Count = 0 Then Return Nothing
            Return TryCast(_sourcesView.SelectedItems(0).Tag, PluginSourceEntity)
        End Function

        ''' <summary>All CHECKED catalog entries — the batch the
        ''' Install button acts on.</summary>
        Private Function CheckedCatalogEntries() As List(Of CatalogEntry)
            Dim entries As New List(Of CatalogEntry)
            For Each item As ListViewItem In _catalogView.CheckedItems
                Dim entry = TryCast(item.Tag, CatalogEntry)
                If entry IsNot Nothing Then entries.Add(entry)
            Next
            Return entries
        End Function

        Private Sub ResetCatalogSelectAll()
            _suppressCheckEvents = True
            Try
                If _catalogSelectAll IsNot Nothing Then _catalogSelectAll.Checked = False
            Finally
                _suppressCheckEvents = False
            End Try
            UpdateDownloadButton()
        End Sub

        Private Sub OnCatalogSelectAllChanged(sender As Object, e As EventArgs)
            If _suppressCheckEvents Then Return
            _suppressCheckEvents = True
            Try
                For Each item As ListViewItem In _catalogView.Items
                    item.Checked = _catalogSelectAll.Checked
                Next
            Finally
                _suppressCheckEvents = False
            End Try
            UpdateDownloadButton()
        End Sub

        Private Sub OnCatalogItemChecked()
            If _suppressCheckEvents Then Return
            ' Sync the select-all box without re-triggering it.
            _suppressCheckEvents = True
            Try
                _catalogSelectAll.Checked = _catalogView.Items.Count > 0 AndAlso
                                            _catalogView.CheckedItems.Count = _catalogView.Items.Count
            Finally
                _suppressCheckEvents = False
            End Try
            UpdateDownloadButton()
        End Sub

        Private Sub UpdateDownloadButton()
            Dim count = _catalogView.CheckedItems.Count
            _downloadButton.Enabled = count > 0
            _downloadButton.Text = If(count > 1, $"Install ({count})...", "Install...")
        End Sub

        Private Sub UpdateButtons()
            Dim src = SelectedSource()
            Dim has = src IsNot Nothing
            _editButton.Enabled = has AndAlso Not src.IsOfficial
            _removeButton.Enabled = has AndAlso Not src.IsOfficial
            _toggleButton.Enabled = has
            _refreshButton.Enabled = has
            If has Then _toggleButton.Text = If(src.IsEnabled, "Disable", "Enable")
        End Sub

        Private Sub OnSourceSelectionChanged()
            UpdateButtons()
            Dim src = SelectedSource()
            If src Is Nothing Then
                _catalogView.Items.Clear()
                ResetCatalogSelectAll()
                _statusLabel.Text = ""
                Return
            End If
            LoadCatalogForSelected(False)
        End Sub

        Private Sub OnRefreshCatalog()
            If SelectedSource() Is Nothing Then Return
            LoadCatalogForSelected(True)
        End Sub

        ''' <summary>
        ''' Fetch + render the selected source's catalog. Named Async Sub
        ''' (not a lambda) so the await resumes on the UI thread and we
        ''' can touch controls directly afterward.
        ''' </summary>
        Private Async Sub LoadCatalogForSelected(forceRefresh As Boolean)
            Dim src = SelectedSource()
            If src Is Nothing OrElse _catalog Is Nothing Then Return

            _catalogView.Items.Clear()
            ResetCatalogSelectAll()
            _refreshButton.Enabled = False
            If Not src.IsEnabled Then
                _statusLabel.Text = "Source is disabled — enable it to browse its catalog."
                _refreshButton.Enabled = True
                Return
            End If
            _statusLabel.Text = $"Loading catalog from {src.Owner}/{src.Repo}…"

            Dim result As CatalogResult
            Try
                result = Await _catalog.GetCatalogAsync(src, forceRefresh)
            Catch ex As Exception
                _statusLabel.Text = $"Catalog load failed: {ex.Message}"
                _refreshButton.Enabled = True
                Return
            End Try

            ' Selection may have changed while awaiting — only render if
            ' the result still matches the selected source.
            Dim current = SelectedSource()
            If current Is Nothing OrElse Not String.Equals(current.SourceId, src.SourceId, StringComparison.Ordinal) Then
                _refreshButton.Enabled = True
                Return
            End If

            _refreshButton.Enabled = True
            If result Is Nothing OrElse Not result.Ok Then
                _statusLabel.Text = If(result IsNot Nothing AndAlso result.ErrorMessage IsNot Nothing,
                                       result.ErrorMessage, "Catalog load failed.")
                Return
            End If

            For Each entry In result.Entries
                Dim m = entry.Manifest
                Dim item As New ListViewItem(If(m.Name, m.Id))
                item.SubItems.Add(If(m.Id, "—"))
                item.SubItems.Add(If(m.Version, "—"))
                item.SubItems.Add(If(m.Author, "—"))
                item.SubItems.Add(If(entry.Origin = PluginOrigin.Official, "Official", "Third-party"))
                item.SubItems.Add(entry.FileName)
                item.Tag = entry
                _catalogView.Items.Add(item)
            Next

            _statusLabel.Text = If(result.Entries.Count = 1,
                                   "1 plugin available.",
                                   $"{result.Entries.Count} plugins available.")
        End Sub

        Private Sub OnAdd()
            Using dlg As New PluginSourceEditDialog(Nothing)
                If dlg.ShowDialog(Me) = DialogResult.OK AndAlso dlg.Result IsNot Nothing Then
                    Dim id = _catalog.SaveSource(dlg.Result)
                    If id Is Nothing Then
                        MessageBox.Show(Me, "Couldn't save the source (a source with the same owner/repo/path may already exist).",
                                        "Plugin Sources", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    End If
                    RefreshSources()
                End If
            End Using
        End Sub

        Private Sub OnEdit()
            Dim src = SelectedSource()
            If src Is Nothing OrElse src.IsOfficial Then Return
            Using dlg As New PluginSourceEditDialog(src)
                If dlg.ShowDialog(Me) = DialogResult.OK AndAlso dlg.Result IsNot Nothing Then
                    _catalog.SaveSource(dlg.Result)
                    RefreshSources()
                End If
            End Using
        End Sub

        Private Sub OnRemove()
            Dim src = SelectedSource()
            If src Is Nothing OrElse src.IsOfficial Then Return
            If MessageBox.Show(Me, $"Remove the source ""{src.DisplayName}""? Installed plugins from it stay installed.",
                               "Plugin Sources", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return
            _catalog.DeleteSource(src.SourceId)
            RefreshSources()
        End Sub

        Private Sub OnToggleEnabled()
            Dim src = SelectedSource()
            If src Is Nothing Then Return
            src.IsEnabled = Not src.IsEnabled
            _catalog.SaveSource(src)
            RefreshSources()
        End Sub

        ''' <summary>
        ''' Phase 6-3/6-4 — stage every selected catalog entry, then
        ''' offer ONE combined install consent (per-plugin warnings
        ''' inlined), install the lot, and reload ONCE at the end.
        ''' Plugins\ is never touched until the install step. Async Sub
        ''' so awaits resume on the UI thread.
        ''' </summary>
        Private Async Sub OnDownload()
            Dim entries = CheckedCatalogEntries()
            If entries.Count = 0 Then Return
            Dim stager = ManagerProgram.Services.GetService(Of PluginStageService)()
            If stager Is Nothing Then
                MessageBox.Show(Me, "The plugin staging service isn't available.", "Install Plugins",
                                MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            _downloadButton.Enabled = False

            ' 1) Stage everything selected, sequentially.
            Dim stagedList As New List(Of StagedPlugin)
            Dim stageFailures As New List(Of String)
            Dim index = 0
            For Each entry In entries
                index += 1
                _statusLabel.Text = $"Downloading {index}/{entries.Count}: {entry.FileName}…"
                Dim result As PluginStageResult
                Try
                    result = Await stager.StageAsync(entry)
                Catch ex As Exception
                    result = New PluginStageResult With {.Ok = False, .ErrorMessage = ex.Message}
                End Try
                If result IsNot Nothing AndAlso result.Ok Then
                    stagedList.Add(result.Staged)
                Else
                    Dim what = If(entry.Manifest?.Id, entry.FileName)
                    stageFailures.Add($"{what}: {If(result?.ErrorMessage, "download failed")}")
                End If
            Next

            UpdateDownloadButton()
            If stagedList.Count = 0 Then
                _statusLabel.Text = "Nothing was staged."
                MessageBox.Show(Me, "No plugins could be staged:" & Environment.NewLine & Environment.NewLine &
                                "  • " & String.Join(Environment.NewLine & "  • ", stageFailures),
                                "Install Plugins", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' 2) One combined consent. Per-plugin warnings (bare third-
            ' party id, collision with a loaded plugin) are inlined under
            ' the plugin they belong to; No leaves everything staged.
            Dim anyWarnings = False
            Dim lines As New List(Of String)
            For Each sp In stagedList
                lines.Add($"• {sp.PluginId} {sp.Version}")
                ' Phase 7-3 — declared capabilities, shown for consent.
                If sp.Capabilities IsNot Nothing AndAlso sp.Capabilities.Count > 0 Then
                    lines.Add($"      Requires: {String.Join(", ", sp.Capabilities)}")
                    If sp.Capabilities.Contains("web-capture") Then
                        lines.Add("      This plugin may ask you to log into a website in an embedded browser and will receive the resulting session cookies.")
                    End If
                End If
                ' Phase 7-3b — static source audit notes (advisory).
                If sp.AuditNotes IsNot Nothing AndAlso sp.AuditNotes.Count > 0 Then
                    For Each note In sp.AuditNotes
                        lines.Add($"      ⓘ {note}")
                    Next
                End If
                If sp.Warnings IsNot Nothing AndAlso sp.Warnings.Count > 0 Then
                    anyWarnings = True
                    For Each w In sp.Warnings
                        lines.Add($"      ⚠ {w}")
                    Next
                End If
            Next

            Dim prompt = If(stagedList.Count = 1,
                            "Downloaded and validated 1 plugin:",
                            $"Downloaded and validated {stagedList.Count} plugins:") &
                         Environment.NewLine & Environment.NewLine &
                         String.Join(Environment.NewLine, lines)
            If stageFailures.Count > 0 Then
                prompt &= Environment.NewLine & Environment.NewLine &
                          $"({stageFailures.Count} could not be staged and will be skipped.)"
            End If
            prompt &= Environment.NewLine & Environment.NewLine &
                      If(stagedList.Count = 1,
                         "Install it now? This copies it into the Plugins folder and reloads plugins. (No keeps it staged without installing.)",
                         "Install them now? This copies them into the Plugins folder and reloads plugins once. (No keeps them staged without installing.)")

            If MessageBox.Show(Me, prompt, "Install Plugins", MessageBoxButtons.YesNo,
                               If(anyWarnings, MessageBoxIcon.Warning, MessageBoxIcon.Question)) <> DialogResult.Yes Then
                _statusLabel.Text = $"Staged {stagedList.Count} plugin(s) — not installed."
                Return
            End If

            ' 3) Install the lot, then reload ONCE.
            Dim installed As New List(Of String)
            Dim installFailures As New List(Of String)
            For Each sp In stagedList
                Dim installResult = stager.InstallStaged(sp.PluginId)
                If installResult IsNot Nothing AndAlso installResult.Ok Then
                    installed.Add(sp.PluginId)
                Else
                    installFailures.Add($"{sp.PluginId}: {If(installResult?.ErrorMessage, "install failed")}")
                End If
            Next

            Dim reloadNote = ""
            Dim reloadErrors = 0
            If installed.Count > 0 Then
                Try
                    Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                    Dim orphanDetector = ManagerProgram.Services.GetService(Of PluginOrphanDetector)()
                    If registry IsNot Nothing Then
                        Dim summary = registry.ReloadAll(orphanDetector)
                        reloadErrors = summary.CompilationErrors.Count
                        reloadNote = $" Plugins reloaded ({summary.LoadedPlugins.Count} loaded, {reloadErrors} errors)."
                    End If
                Catch ex As Exception
                    reloadNote = $" Reload failed: {ex.Message}"
                    reloadErrors = -1
                End Try
            End If

            _statusLabel.Text = $"Installed {installed.Count} plugin(s).{reloadNote}"

            Dim summaryMsg = If(installed.Count = 1,
                                $"""{installed(0)}"" is installed.",
                                $"{installed.Count} plugins installed.")
            If installFailures.Count > 0 OrElse stageFailures.Count > 0 Then
                Dim problems = New List(Of String)
                problems.AddRange(stageFailures)
                problems.AddRange(installFailures)
                summaryMsg &= Environment.NewLine & Environment.NewLine & "Problems:" & Environment.NewLine &
                              "  • " & String.Join(Environment.NewLine & "  • ", problems)
            End If
            If reloadErrors <> 0 Then
                summaryMsg &= Environment.NewLine & Environment.NewLine &
                              "The plugin reload reported errors — check the Status tab for details."
            End If

            MessageBox.Show(Me, summaryMsg, "Install Plugins", MessageBoxButtons.OK,
                            If(installFailures.Count > 0 OrElse reloadErrors <> 0, MessageBoxIcon.Warning, MessageBoxIcon.Information))
        End Sub

    End Class

    ''' <summary>
    ''' Phase 6-2 — add/edit dialog for a plugin source. Returns the
    ''' edited entity in Result on OK (SourceId preserved for edits,
    ''' empty for a new source so the service inserts).
    ''' </summary>
    Public Class PluginSourceEditDialog
        Inherits Form

        Private ReadOnly _editing As PluginSourceEntity
        Private _nameBox As TextBox
        Private _ownerBox As TextBox
        Private _repoBox As TextBox
        Private _pathBox As TextBox
        Private _branchBox As TextBox
        Private _enabledCheck As CheckBox

        ''' <summary>The edited source, or Nothing if cancelled.</summary>
        Public Property Result As PluginSourceEntity

        Public Sub New(editing As PluginSourceEntity)
            _editing = editing
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
        End Sub

        Private Sub InitializeControls()
            Me.Text = If(_editing Is Nothing, "Add Plugin Source", "Edit Plugin Source")
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.ClientSize = New Size(420, 250)

            Dim y = 16
            _nameBox = AddRow("Display name", y) : y += 38
            _ownerBox = AddRow("GitHub owner", y) : y += 38
            _repoBox = AddRow("Repository", y) : y += 38
            _pathBox = AddRow("Path in repo (optional)", y) : y += 38
            _branchBox = AddRow("Branch", y) : y += 38

            _enabledCheck = New CheckBox With {
                .Text = "Enabled", .Location = New Point(150, y), .AutoSize = True, .Checked = True}
            Me.Controls.Add(_enabledCheck)

            Dim okButton As New Button With {
                .Text = "OK", .Location = New Point(244, 212), .Size = New Size(80, 28)}
            AddHandler okButton.Click, Sub(s, e) OnOk()
            Me.Controls.Add(okButton)

            Dim cancelButton As New Button With {
                .Text = "Cancel", .Location = New Point(330, 212), .Size = New Size(80, 28),
                .DialogResult = DialogResult.Cancel}
            Me.Controls.Add(cancelButton)
            Me.CancelButton = cancelButton

            If _editing IsNot Nothing Then
                _nameBox.Text = _editing.DisplayName
                _ownerBox.Text = _editing.Owner
                _repoBox.Text = _editing.Repo
                _pathBox.Text = _editing.RepoPath
                _branchBox.Text = _editing.Branch
                _enabledCheck.Checked = _editing.IsEnabled
            Else
                _branchBox.Text = "master"
            End If
        End Sub

        Private Function AddRow(labelText As String, y As Integer) As TextBox
            Dim lbl As New Label With {.Text = labelText, .Location = New Point(12, y + 3), .Size = New Size(132, 20)}
            Me.Controls.Add(lbl)
            Dim box As New TextBox With {.Location = New Point(150, y), .Size = New Size(258, 23)}
            Me.Controls.Add(box)
            Return box
        End Function

        Private Sub OnOk()
            If String.IsNullOrWhiteSpace(_nameBox.Text) OrElse
               String.IsNullOrWhiteSpace(_ownerBox.Text) OrElse
               String.IsNullOrWhiteSpace(_repoBox.Text) Then
                MessageBox.Show(Me, "Display name, owner, and repository are required.",
                                "Plugin Source", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Result = New PluginSourceEntity With {
                .SourceId = If(_editing IsNot Nothing, _editing.SourceId, Nothing),
                .DisplayName = _nameBox.Text.Trim(),
                .Owner = _ownerBox.Text.Trim(),
                .Repo = _repoBox.Text.Trim(),
                .RepoPath = _pathBox.Text.Trim(),
                .Branch = If(String.IsNullOrWhiteSpace(_branchBox.Text), "master", _branchBox.Text.Trim()),
                .IsEnabled = _enabledCheck.Checked,
                .IsOfficial = (_editing IsNot Nothing AndAlso _editing.IsOfficial)
            }
            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

    End Class

End Namespace
