Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  ManagedFilesPanel — UI for one ManagedDirectory entry
'
'  Hosted inside a TabPage on InstancePanel. One panel per
'  managed directory (saves, mods, etc); all of them share
'  this single class.
'
'  Talks to the node via the file-ops wrapper added in Phase
'  4c-1: ListFilesAsync / DownloadFileAsync / UploadFileAsync /
'  DeleteFileAsync. Whitelist enforcement happens both here
'  (Upload/Delete buttons disabled per the directory's
'  Permissions flags) and on the node (every request validates
'  against the allowedRoots and allowedExtensions sent as
'  query params), so a Manager bug or stale UI state can't
'  bypass the contract.
'
'  Resolution flow: panel knows the instance id. To act on the
'  node it needs (a) the install path and (b) the node client.
'  Both come from a single DB query — Instance → Installation
'  → Node — done on every call rather than cached so an
'  out-of-band update to the auth token or host address takes
'  effect on the next op without panel rebuild. Cost is one
'  Find-by-PK against an in-memory SQLite — negligible.
' ============================================================

Namespace GSM.Manager.UI

    Public Class ManagedFilesPanel
        Inherits UserControl

        Private ReadOnly _instanceId As String
        Private ReadOnly _directory As ManagedDirectory

        ' UI controls
        Private _toolbar As Panel
        Private _refreshButton As Button
        Private _generateNewButton As Button
        Private _uploadButton As Button
        Private _downloadButton As Button
        Private _duplicateButton As Button
        Private _renameButton As Button
        Private _deleteButton As Button
        Private _listView As ListView
        Private _statusLabel As Label

        ' True while a file op is in flight; gates Upload/Download/
        ' Delete buttons so the user can't kick off overlapping
        ' operations against the same listing. Refresh stays enabled
        ' even mid-op so a stalled call can be replaced with a fresh
        ' one (the old CTS gets cancelled).
        Private _opInFlight As Boolean

        ' Cancellation source for the in-flight list refresh.
        ' Disposed and replaced on every RefreshClicked so a slow
        ' previous call doesn't clobber a more recent one.
        Private _refreshCts As System.Threading.CancellationTokenSource

        Public Sub New(instanceId As String, directory As ManagedDirectory)
            _instanceId = instanceId
            _directory = directory
            _refreshCts = New System.Threading.CancellationTokenSource()
            InitializeControls()
            ' Kick off the initial list. Fire-and-forget; the async
            ' resumption updates UI on the captured SyncContext.
            Dim _unused = RefreshAsync(_refreshCts.Token)
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _refreshCts IsNot Nothing Then
                Try
                    _refreshCts.Cancel()
                    _refreshCts.Dispose()
                Catch
                End Try
                _refreshCts = Nothing
            End If
            MyBase.Dispose(disposing)
        End Sub

        Private Sub InitializeControls()
            ' Toolbar at top: Refresh, Upload, Download, Delete.
            _toolbar = New Panel() With {
                .Dock = DockStyle.Top,
                .Height = 40
            }

            _refreshButton = New Button() With {
                .Text = "Refresh",
                .Size = New Size(90, 28),
                .Location = New Point(10, 6)
            }
            AddHandler _refreshButton.Click, Sub(s, e) RefreshClicked()

            ' Plugin's IFileGenerationProvider gates this button.
            ' We probe at construction time and resolve the button
            ' label / tab title from the plugin so the affordance
            ' is fully plugin-defined — "Generate New Map..." for
            ' Factorio, but a future plugin could surface
            ' "New Configuration..." or "Create from Template..."
            ' on the same panel shell. The button is also gated on
            ' the plugin's GetTargetDirectoryRef matching THIS
            ' panel's directory — a plugin that opts in for saves
            ' but not mods only sees the button on the saves tab.
            Dim genInfo = ResolveFileGenerationInfo()
            Dim hasGen = (genInfo IsNot Nothing)
            _generateNewButton = New Button() With {
                .Text = If(hasGen, genInfo.ButtonLabel, "Generate New..."),
                .Size = New Size(150, 28),
                .Location = New Point(110, 6),
                .Visible = hasGen
            }
            AddHandler _generateNewButton.Click, Sub(s, e) GenerateNewClicked(genInfo)

            ' Shift the rest of the toolbar right when the Generate
            ' button is shown so the layout doesn't have a hole.
            ' Width + gap matches the button's footprint.
            Dim shift = If(hasGen, 160, 0)

            _uploadButton = New Button() With {
                .Text = "Upload...",
                .Size = New Size(110, 28),
                .Location = New Point(110 + shift, 6)
            }
            AddHandler _uploadButton.Click, Sub(s, e) UploadClicked()

            _downloadButton = New Button() With {
                .Text = "Download...",
                .Size = New Size(110, 28),
                .Location = New Point(230 + shift, 6)
            }
            AddHandler _downloadButton.Click, Sub(s, e) DownloadClicked()

            _duplicateButton = New Button() With {
                .Text = "Duplicate...",
                .Size = New Size(100, 28),
                .Location = New Point(350 + shift, 6)
            }
            AddHandler _duplicateButton.Click, Sub(s, e) DuplicateClicked()

            _renameButton = New Button() With {
                .Text = "Rename...",
                .Size = New Size(90, 28),
                .Location = New Point(460 + shift, 6)
            }
            AddHandler _renameButton.Click, Sub(s, e) RenameClicked()

            _deleteButton = New Button() With {
                .Text = "Delete",
                .Size = New Size(90, 28),
                .Location = New Point(560 + shift, 6)
            }
            AddHandler _deleteButton.Click, Sub(s, e) DeleteClicked()

            ' Read-only directory? Make the disabled state visible
            ' rather than hiding the buttons entirely — users get a
            ' clear signal that the capability is intentionally
            ' restricted, not an unfinished feature.
            If (_directory.Permissions And DirPermissions.Write) = 0 Then
                _uploadButton.Text = "Upload (read-only)"
                _duplicateButton.Text = "Duplicate (read-only)"
            End If
            If (_directory.Permissions And DirPermissions.Delete) = 0 Then
                _deleteButton.Text = "Delete (read-only)"
            End If
            ' Rename is effectively "write new + delete old", so the
            ' button is only meaningful when both flags are present.
            ' Surface read-only labelling whenever either is missing.
            If (_directory.Permissions And (DirPermissions.Write Or DirPermissions.Delete)) <>
               (DirPermissions.Write Or DirPermissions.Delete) Then
                _renameButton.Text = "Rename (read-only)"
            End If

            _toolbar.Controls.AddRange(New Control() {
                _refreshButton, _generateNewButton, _uploadButton, _downloadButton,
                _duplicateButton, _renameButton, _deleteButton
            })

            ' Status label at bottom — communicates progress and
            ' the result of the last op. Single line; messages
            ' replace each other.
            _statusLabel = New Label() With {
                .Dock = DockStyle.Bottom,
                .Height = 22,
                .Padding = New Padding(10, 4, 10, 0),
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.Gray,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Text = "Loading..."
            }

            ' ListView fills the rest. Newest-first order applied
            ' in ApplyFiles so a fresh upload lands at the top.
            ' Double-click downloads as a convenience.
            _listView = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .GridLines = True,
                .HideSelection = False,
                .MultiSelect = False
            }
            _listView.Columns.Add("Name", 280)
            _listView.Columns.Add("Size", 100)
            _listView.Columns.Add("Modified", 160)
            AddHandler _listView.DoubleClick, AddressOf OnListDoubleClick
            AddHandler _listView.SelectedIndexChanged, AddressOf OnSelectionChanged

            ' Add Fill child first so docked children claim edges
            ' before the listview gets the remainder.
            Me.Controls.Add(_listView)
            Me.Controls.Add(_statusLabel)
            Me.Controls.Add(_toolbar)

            RecomputeButtonState()
        End Sub

        Private Sub OnListDoubleClick(sender As Object, e As EventArgs)
            If _downloadButton.Enabled Then DownloadClicked()
        End Sub

        Private Sub OnSelectionChanged(sender As Object, e As EventArgs)
            RecomputeButtonState()
        End Sub

        ''' <summary>
        ''' Sets all four buttons based on the panel's current
        ''' state: whether an op is in flight, whether the
        ''' directory grants write/delete, and whether the listview
        ''' has a selection. Single source of truth so OnSelectionChanged
        ''' and op start/end paths can't disagree.
        ''' </summary>
        Private Sub RecomputeButtonState()
            If Me.IsDisposed Then Return
            Dim notBusy = Not _opInFlight
            Dim canWrite = (_directory.Permissions And DirPermissions.Write) <> 0
            Dim canDelete = (_directory.Permissions And DirPermissions.Delete) <> 0
            ' Rename requires both: write to materialise the new
            ' name, delete to remove the old one. Either flag
            ' missing makes rename meaningless on this directory.
            ' Duplicate only needs Write — source is preserved — so
            ' a write-only directory still gets duplication.
            Dim canRename = canWrite AndAlso canDelete
            Dim canDuplicate = canWrite
            Dim hasSelection = _listView.SelectedItems.Count > 0

            _refreshButton.Enabled = notBusy
            ' Generate is gated on Write — a generated map needs
            ' to land somewhere on disk. notBusy gates against
            ' colliding with a list refresh that would clobber the
            ' new file's display.
            If _generateNewButton IsNot Nothing Then
                _generateNewButton.Enabled = notBusy AndAlso canWrite
            End If
            _uploadButton.Enabled = notBusy AndAlso canWrite
            _downloadButton.Enabled = notBusy AndAlso hasSelection
            _duplicateButton.Enabled = notBusy AndAlso canDuplicate AndAlso hasSelection
            _renameButton.Enabled = notBusy AndAlso canRename AndAlso hasSelection
            _deleteButton.Enabled = notBusy AndAlso canDelete AndAlso hasSelection
        End Sub

        Private Sub RefreshClicked()
            ' Cancel any pending refresh and start a new one. New
            ' CTS so the previous one is fully drained before
            ' being replaced.
            Dim oldCts = _refreshCts
            _refreshCts = New System.Threading.CancellationTokenSource()
            Try
                If oldCts IsNot Nothing Then
                    oldCts.Cancel()
                    oldCts.Dispose()
                End If
            Catch
            End Try
            Dim _unused = RefreshAsync(_refreshCts.Token)
        End Sub

        ' ====================================================
        '  Refresh / file listing
        ' ====================================================

        Private Async Function RefreshAsync(token As System.Threading.CancellationToken) As Task
            _opInFlight = True
            RecomputeButtonState()
            SetStatus("Loading...", Color.Gray)
            Try
                Dim resolved = ResolveNode()
                If resolved Is Nothing Then
                    SetStatus("Could not resolve node for instance.", Color.Firebrick)
                    Return
                End If

                Dim allowedRoots As IReadOnlyList(Of String) =
                    New String() {_directory.RelativePath}
                Dim files = Await resolved.Client.ListFilesAsync(
                    _instanceId,
                    resolved.InstallPath,
                    _directory.RelativePath,
                    allowedRoots,
                    _directory.AllowedExtensions,
                    token)
                If token.IsCancellationRequested OrElse Me.IsDisposed Then Return
                ApplyFiles(files)
            Catch ex As OperationCanceledException
                ' Caller cancelled (panel disposed or refresh
                ' re-triggered). No status update — the new refresh
                ' will overwrite it.
            Catch ex As NodeApiException When ex.StatusCode.HasValue AndAlso
                                                ex.StatusCode.Value = HttpStatusCode.NotFound
                ' 404 here means either the install path is gone
                ' (rare; would also affect every other op against
                ' this instance) or the managed subdirectory itself
                ' doesn't exist on disk yet. The latter is normal
                ' for fresh installs of games that create their
                ' state dirs on first run — Factorio's saves/, for
                ' example, doesn't appear until the server has
                ' completed at least one map load. Render as empty
                ' rather than as an error so the panel matches the
                ' user's mental model: "no files yet, the dir hasn't
                ' been used". Upload still works on the node side
                ' because the upload endpoint creates the parent
                ' dir on demand.
                If Not Me.IsDisposed Then
                    ApplyFiles(New List(Of FileEntry))
                End If
            Catch ex As Exception
                If Not Me.IsDisposed Then
                    SetStatus($"Failed to list files: {ex.Message}", Color.Firebrick)
                End If
            Finally
                _opInFlight = False
                RecomputeButtonState()
            End Try
        End Function

        Private Sub ApplyFiles(files As IReadOnlyList(Of FileEntry))
            _listView.BeginUpdate()
            Try
                _listView.Items.Clear()
                If files Is Nothing OrElse files.Count = 0 Then
                    SetStatus($"No files in {_directory.DisplayName}.", Color.Gray)
                    Return
                End If

                ' Sort newest-modified first so a fresh upload
                ' lands at the top. Matches what users expect
                ' from "saves" specifically.
                Dim ordered = files.OrderByDescending(Function(f) f.ModifiedUtc).ToList()
                For Each entry In ordered
                    Dim displayName = ShortName(entry.RelativePath)
                    Dim item As New ListViewItem(displayName)
                    item.SubItems.Add(FormatSize(entry.SizeBytes))
                    item.SubItems.Add(entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                    item.Tag = entry
                    _listView.Items.Add(item)
                Next
                Dim noun = If(files.Count = 1, "file", "files")
                SetStatus($"{files.Count} {noun}.", Color.DarkGreen)
            Finally
                _listView.EndUpdate()
            End Try
        End Sub

        ' ====================================================
        '  Upload
        ' ====================================================

        Private Async Sub UploadClicked()
            Dim filter = BuildFileFilter()
            Dim sourcePath As String = Nothing
            Using dlg As New OpenFileDialog()
                dlg.Title = $"Upload to {_directory.DisplayName}"
                dlg.Multiselect = False
                If Not String.IsNullOrEmpty(filter) Then dlg.Filter = filter
                If dlg.ShowDialog(Me) <> DialogResult.OK Then Return
                sourcePath = dlg.FileName
            End Using
            Await UploadFileAsync(sourcePath)
        End Sub

        Private Async Function UploadFileAsync(sourcePath As String) As Task
            Dim resolved = ResolveNode()
            If resolved Is Nothing Then
                SetStatus("Could not resolve node for instance.", Color.Firebrick)
                Return
            End If

            Dim fileName = Path.GetFileName(sourcePath)
            Dim relativePath = _directory.RelativePath & "/" & fileName
            Dim allowedRoots As IReadOnlyList(Of String) =
                New String() {_directory.RelativePath}

            ' Confirm overwrite if the listview already has a file
            ' with this name. Less abrupt than letting the node
            ' silently overwrite or letting the request 409 with
            ' overwrite=false.
            Dim alreadyExists = _listView.Items.Cast(Of ListViewItem)().
                Any(Function(it) String.Equals(it.Text, fileName, StringComparison.OrdinalIgnoreCase))
            If alreadyExists Then
                Dim resp = MessageBox.Show(Me,
                    $"A file named '{fileName}' already exists in {_directory.DisplayName}. Overwrite?",
                    "Confirm overwrite",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                If resp <> DialogResult.Yes Then Return
            End If

            _opInFlight = True
            RecomputeButtonState()
            SetStatus($"Uploading {fileName}...", Color.DarkOrange)
            Try
                Dim uploadedSize As Long = 0
                Using fs As New FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim entry = Await resolved.Client.UploadFileAsync(
                        _instanceId,
                        resolved.InstallPath,
                        relativePath,
                        allowedRoots,
                        _directory.AllowedExtensions,
                        fs,
                        overwrite:=True,
                        cancellation:=System.Threading.CancellationToken.None)
                    If entry IsNot Nothing Then uploadedSize = entry.SizeBytes
                End Using
                If Me.IsDisposed Then Return
                SetStatus($"Uploaded {fileName} ({FormatSize(uploadedSize)}).", Color.DarkGreen)
                ' Refresh to pick up the new file (and remove the old
                ' one if it was overwritten).
                Await RefreshAsync(System.Threading.CancellationToken.None)
            Catch ex As Exception
                If Not Me.IsDisposed Then SetStatus($"Upload failed: {ex.Message}", Color.Firebrick)
            Finally
                _opInFlight = False
                If Not Me.IsDisposed Then RecomputeButtonState()
            End Try
        End Function

        ' ====================================================
        '  Download
        ' ====================================================

        Private Async Sub DownloadClicked()
            If _listView.SelectedItems.Count = 0 Then Return
            Dim entry = TryCast(_listView.SelectedItems(0).Tag, FileEntry)
            If entry Is Nothing Then Return

            Dim suggestedName = ShortName(entry.RelativePath)
            Dim filter = BuildFileFilter()
            Dim destination As String = Nothing
            Using dlg As New SaveFileDialog()
                dlg.Title = $"Download {suggestedName}"
                dlg.FileName = suggestedName
                If Not String.IsNullOrEmpty(filter) Then dlg.Filter = filter
                If dlg.ShowDialog(Me) <> DialogResult.OK Then Return
                destination = dlg.FileName
            End Using
            Await DownloadFileAsync(entry, destination)
        End Sub

        Private Async Function DownloadFileAsync(entry As FileEntry, destination As String) As Task
            Dim resolved = ResolveNode()
            If resolved Is Nothing Then
                SetStatus("Could not resolve node for instance.", Color.Firebrick)
                Return
            End If

            Dim displayName = ShortName(entry.RelativePath)
            Dim allowedRoots As IReadOnlyList(Of String) =
                New String() {_directory.RelativePath}

            _opInFlight = True
            RecomputeButtonState()
            SetStatus($"Downloading {displayName}...", Color.DarkOrange)
            Dim ok As Boolean = False
            Try
                Using fs As New FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)
                    Await resolved.Client.DownloadFileAsync(
                        _instanceId,
                        resolved.InstallPath,
                        entry.RelativePath,
                        allowedRoots,
                        _directory.AllowedExtensions,
                        fs,
                        cancellation:=System.Threading.CancellationToken.None)
                End Using
                ok = True
                If Me.IsDisposed Then Return
                SetStatus($"Downloaded {displayName} to {destination}.", Color.DarkGreen)
            Catch ex As Exception
                If Not Me.IsDisposed Then SetStatus($"Download failed: {ex.Message}", Color.Firebrick)
            Finally
                _opInFlight = False
                If Not Me.IsDisposed Then RecomputeButtonState()
                If Not ok Then
                    ' Don't leave a half-written corrupt file behind.
                    ' Best-effort delete; the user might still be
                    ' opening it in another app, in which case the
                    ' delete fails harmlessly.
                    Try : File.Delete(destination) : Catch : End Try
                End If
            End Try
        End Function

        ' ====================================================
        '  Delete
        ' ====================================================

        Private Async Sub DeleteClicked()
            If _listView.SelectedItems.Count = 0 Then Return
            Dim entry = TryCast(_listView.SelectedItems(0).Tag, FileEntry)
            If entry Is Nothing Then Return

            Dim displayName = ShortName(entry.RelativePath)
            Dim resp = MessageBox.Show(Me,
                $"Delete {displayName} from {_directory.DisplayName}?",
                "Confirm delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If resp <> DialogResult.Yes Then Return

            Await DeleteFileAsync(entry)
        End Sub

        Private Async Function DeleteFileAsync(entry As FileEntry) As Task
            Dim resolved = ResolveNode()
            If resolved Is Nothing Then
                SetStatus("Could not resolve node for instance.", Color.Firebrick)
                Return
            End If

            Dim displayName = ShortName(entry.RelativePath)
            Dim allowedRoots As IReadOnlyList(Of String) =
                New String() {_directory.RelativePath}

            _opInFlight = True
            RecomputeButtonState()
            SetStatus($"Deleting {displayName}...", Color.DarkOrange)
            Try
                Dim deleted = Await resolved.Client.DeleteFileAsync(
                    _instanceId,
                    resolved.InstallPath,
                    entry.RelativePath,
                    allowedRoots,
                    _directory.AllowedExtensions,
                    System.Threading.CancellationToken.None)
                If Me.IsDisposed Then Return
                If deleted Then
                    SetStatus($"Deleted {displayName}.", Color.DarkGreen)
                Else
                    SetStatus($"{displayName} was already gone (no change).", Color.Gray)
                End If
                Await RefreshAsync(System.Threading.CancellationToken.None)
            Catch ex As Exception
                If Not Me.IsDisposed Then SetStatus($"Delete failed: {ex.Message}", Color.Firebrick)
            Finally
                _opInFlight = False
                If Not Me.IsDisposed Then RecomputeButtonState()
            End Try
        End Function

        ' ====================================================
        '  Rename
        ' ====================================================

        Private Async Sub RenameClicked()
            If _listView.SelectedItems.Count = 0 Then Return
            Dim entry = TryCast(_listView.SelectedItems(0).Tag, FileEntry)
            If entry Is Nothing Then Return

            Dim oldName = ShortName(entry.RelativePath)

            ' Microsoft.VisualBasic.Interaction.InputBox — the cheap
            ' built-in single-line prompt. Returns String.Empty when
            ' the user clicks Cancel OR enters an empty string; both
            ' cases bail without further work, so we don't need to
            ' disambiguate. If a polished rename UI is wanted later,
            ' replace this call with a custom Form (basename pre-
            ' selected, extension shown as a fixed suffix, inline
            ' validation feedback) without changing anything below.
            Dim newName = Microsoft.VisualBasic.Interaction.InputBox(
                $"Enter the new name for '{oldName}':",
                $"Rename in {_directory.DisplayName}",
                oldName)
            If String.IsNullOrWhiteSpace(newName) Then Return
            newName = newName.Trim()

            ' Same-name no-op. The node would also detect this and
            ' return 200 with the existing entry, but skipping the
            ' round trip is cleaner.
            If newName.Equals(oldName, StringComparison.OrdinalIgnoreCase) Then Return

            ' Client-side guards — the node validates these too,
            ' but a friendly local error beats a generic 400.
            If newName.Contains("/"c) OrElse newName.Contains("\"c) Then
                MessageBox.Show(Me, "New name must not contain path separators.",
                                 "Invalid name",
                                 MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            If _directory.AllowedExtensions IsNot Nothing AndAlso _directory.AllowedExtensions.Count > 0 Then
                Dim ext = Path.GetExtension(newName)
                Dim allowed = _directory.AllowedExtensions.Any(
                    Function(e)
                        Dim normalised = If(e.StartsWith("."), e, "." & e)
                        Return String.Equals(normalised, ext, StringComparison.OrdinalIgnoreCase)
                    End Function)
                If Not allowed Then
                    Dim joined = String.Join(", ", _directory.AllowedExtensions)
                    MessageBox.Show(Me,
                        $"New name must end with one of: {joined}.",
                        "Invalid extension",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
            End If

            Await RenameFileAsync(entry, newName)
        End Sub

        Private Async Function RenameFileAsync(entry As FileEntry, newName As String) As Task
            Dim resolved = ResolveNode()
            If resolved Is Nothing Then
                SetStatus("Could not resolve node for instance.", Color.Firebrick)
                Return
            End If

            Dim oldDisplayName = ShortName(entry.RelativePath)
            Dim newRelativePath = _directory.RelativePath & "/" & newName
            Dim allowedRoots As IReadOnlyList(Of String) =
                New String() {_directory.RelativePath}

            ' Confirm overwrite if the listview already has a file
            ' under the new name. Same UX as upload — explicit yes
            ' beats a node-side 409 and a confused user.
            Dim overwrite As Boolean = False
            Dim collision = _listView.Items.Cast(Of ListViewItem)().
                Any(Function(it) String.Equals(it.Text, newName, StringComparison.OrdinalIgnoreCase))
            If collision Then
                Dim resp = MessageBox.Show(Me,
                    $"A file named '{newName}' already exists in {_directory.DisplayName}. Overwrite?",
                    "Confirm overwrite",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                If resp <> DialogResult.Yes Then Return
                overwrite = True
            End If

            _opInFlight = True
            RecomputeButtonState()
            SetStatus($"Renaming {oldDisplayName} → {newName}...", Color.DarkOrange)
            Try
                Dim newEntry = Await resolved.Client.RenameFileAsync(
                    _instanceId,
                    resolved.InstallPath,
                    entry.RelativePath,
                    newRelativePath,
                    allowedRoots,
                    _directory.AllowedExtensions,
                    overwrite,
                    System.Threading.CancellationToken.None)
                If Me.IsDisposed Then Return
                SetStatus($"Renamed {oldDisplayName} → {newName}.", Color.DarkGreen)
                Await RefreshAsync(System.Threading.CancellationToken.None)
            Catch ex As Exception
                If Not Me.IsDisposed Then SetStatus($"Rename failed: {ex.Message}", Color.Firebrick)
            Finally
                _opInFlight = False
                If Not Me.IsDisposed Then RecomputeButtonState()
            End Try
        End Function

        ' ====================================================
        '  Duplicate
        ' ====================================================

        Private Async Sub DuplicateClicked()
            If _listView.SelectedItems.Count = 0 Then Return
            Dim entry = TryCast(_listView.SelectedItems(0).Tag, FileEntry)
            If entry Is Nothing Then Return

            Dim oldName = ShortName(entry.RelativePath)
            Dim suggested = SuggestDuplicateName(oldName)

            ' Same prompt mechanism as Rename. The default value is
            ' a pre-computed unused name ("foo - Copy.zip", or the
            ' next free numbered variant), so the user can usually
            ' just hit Enter — the most common case for backup-
            ' before-play is "give me a copy with any sensible
            ' name", not a specific name choice.
            Dim newName = Microsoft.VisualBasic.Interaction.InputBox(
                $"Enter a name for the duplicate of '{oldName}':",
                $"Duplicate in {_directory.DisplayName}",
                suggested)
            If String.IsNullOrWhiteSpace(newName) Then Return
            newName = newName.Trim()

            ' Same-name is invalid for duplicate (the node will
            ' reject it with 400; this is the friendly client-side
            ' equivalent).
            If newName.Equals(oldName, StringComparison.OrdinalIgnoreCase) Then
                MessageBox.Show(Me,
                    "Duplicate name must differ from the source file.",
                    "Invalid name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            If newName.Contains("/"c) OrElse newName.Contains("\"c) Then
                MessageBox.Show(Me, "New name must not contain path separators.",
                                 "Invalid name",
                                 MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            If _directory.AllowedExtensions IsNot Nothing AndAlso _directory.AllowedExtensions.Count > 0 Then
                Dim ext = Path.GetExtension(newName)
                Dim allowed = _directory.AllowedExtensions.Any(
                    Function(e)
                        Dim normalised = If(e.StartsWith("."), e, "." & e)
                        Return String.Equals(normalised, ext, StringComparison.OrdinalIgnoreCase)
                    End Function)
                If Not allowed Then
                    Dim joined = String.Join(", ", _directory.AllowedExtensions)
                    MessageBox.Show(Me,
                        $"New name must end with one of: {joined}.",
                        "Invalid extension",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
            End If

            Await DuplicateFileAsync(entry, newName)
        End Sub

        Private Async Function DuplicateFileAsync(entry As FileEntry, newName As String) As Task
            Dim resolved = ResolveNode()
            If resolved Is Nothing Then
                SetStatus("Could not resolve node for instance.", Color.Firebrick)
                Return
            End If

            Dim oldDisplayName = ShortName(entry.RelativePath)
            Dim newRelativePath = _directory.RelativePath & "/" & newName
            Dim allowedRoots As IReadOnlyList(Of String) =
                New String() {_directory.RelativePath}

            ' Confirm overwrite if the listview already has a file
            ' under the duplicate name. The user may have hand-
            ' edited the suggested name to one that collides; better
            ' to ask than to silently obliterate their existing copy.
            Dim overwrite As Boolean = False
            Dim collision = _listView.Items.Cast(Of ListViewItem)().
                Any(Function(it) String.Equals(it.Text, newName, StringComparison.OrdinalIgnoreCase))
            If collision Then
                Dim resp = MessageBox.Show(Me,
                    $"A file named '{newName}' already exists in {_directory.DisplayName}. Overwrite?",
                    "Confirm overwrite",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                If resp <> DialogResult.Yes Then Return
                overwrite = True
            End If

            _opInFlight = True
            RecomputeButtonState()
            SetStatus($"Duplicating {oldDisplayName} → {newName}...", Color.DarkOrange)
            Try
                Dim newEntry = Await resolved.Client.CopyFileAsync(
                    _instanceId,
                    resolved.InstallPath,
                    entry.RelativePath,
                    newRelativePath,
                    allowedRoots,
                    _directory.AllowedExtensions,
                    overwrite,
                    System.Threading.CancellationToken.None)
                If Me.IsDisposed Then Return
                Dim copiedSize As Long = If(newEntry IsNot Nothing, newEntry.SizeBytes, 0L)
                SetStatus($"Duplicated {oldDisplayName} → {newName} ({FormatSize(copiedSize)}).",
                          Color.DarkGreen)
                Await RefreshAsync(System.Threading.CancellationToken.None)
            Catch ex As Exception
                If Not Me.IsDisposed Then SetStatus($"Duplicate failed: {ex.Message}", Color.Firebrick)
            Finally
                _opInFlight = False
                If Not Me.IsDisposed Then RecomputeButtonState()
            End Try
        End Function

        ' ====================================================
        '  Generate New (file generation)
        ' ====================================================

        ''' <summary>
        ''' Resolved info bundle: the plugin's IFileGenerationProvider
        ''' instance plus the user-facing strings (button label,
        ''' tab title) cached at construction time. Returned by
        ''' ResolveFileGenerationInfo — Nothing means "no Generate
        ''' button on this panel", either because the plugin
        ''' doesn't implement IFileGenerationProvider or because
        ''' the provider's target directory doesn't match this
        ''' panel's directory.
        ''' </summary>
        Private Class FileGenInfo
            Public Provider As IFileGenerationProvider
            Public ButtonLabel As String
            Public TabTitle As String
        End Class

        ''' <summary>
        ''' Probe the plugin for IFileGenerationProvider and check
        ''' whether its target directory matches this panel's
        ''' directory. Returns Nothing if either condition fails;
        ''' otherwise returns the provider plus pre-resolved labels
        ''' (with sensible fallbacks when the plugin returns null/
        ''' empty for them). Resolved once at construction time —
        ''' re-resolving on every click would handle the hot-reload
        ''' case where a plugin gains/loses the interface mid-
        ''' session, but that's a niche we don't pay the complexity
        ''' for here.
        ''' </summary>
        Private Function ResolveFileGenerationInfo() As FileGenInfo
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return Nothing
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then Return Nothing
                    Dim plugin = registry.GetPlugin(instanceEntity.GameId)
                    Dim provider = TryCast(plugin, IFileGenerationProvider)
                    If provider Is Nothing Then Return Nothing

                    Dim targetRef = provider.GetTargetDirectoryRef()
                    If String.IsNullOrEmpty(targetRef) Then Return Nothing
                    ' {InstanceId} substitution mirrors what
                    ' BuildManagedFilesTabs does on the directory
                    ' side — the plugin's literal token (if any)
                    ' has to match this panel's already-substituted
                    ' RelativePath.
                    Dim resolvedTargetRef = targetRef.Replace("{InstanceId}", _instanceId)
                    If Not String.Equals(resolvedTargetRef, _directory.RelativePath,
                                          StringComparison.OrdinalIgnoreCase) Then
                        Return Nothing
                    End If

                    Dim label = If(provider.GetButtonLabel(), "")
                    If String.IsNullOrEmpty(label) Then label = "Generate New..."
                    Dim title = If(provider.GetTabTitle(), "")
                    If String.IsNullOrEmpty(title) Then title = "Generate File"

                    Return New FileGenInfo With {
                        .Provider = provider,
                        .ButtonLabel = label,
                        .TabTitle = title
                    }
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Open the FileGenerationPanel as a sibling tab next to
        ''' this panel's tab. Walks the parent chain to find the
        ''' hosting TabPage and TabControl — fragile if the panel
        ''' is ever embedded somewhere other than a TabPage, but
        ''' that's its only host today and the assumption is
        ''' explicit so a future relocation has a clear thing to
        ''' fix.
        '''
        ''' Callbacks passed to FileGenerationPanel:
        '''   onClose   — user dismissed without success. Tab is
        '''               removed; we switch back to this tab.
        '''   onSuccess — user clicked "Show in Files" after a
        '''               successful generation. Same teardown as
        '''               onClose, plus we trigger a refresh so
        '''               the new file appears in our listing.
        '''
        ''' info is captured at button-construction time (cheap
        ''' — the lookup happens once) and threaded through here
        ''' so the click handler doesn't re-resolve the plugin.
        ''' </summary>
        Private Sub GenerateNewClicked(info As FileGenInfo)
            If info Is Nothing Then Return
            Dim mySourceTab = TryCast(Me.Parent, TabPage)
            If mySourceTab Is Nothing Then Return
            Dim hostTabs = TryCast(mySourceTab.Parent, TabControl)
            If hostTabs Is Nothing Then Return

            Dim genTab As New TabPage(info.TabTitle)

            ' Both callbacks share the same teardown logic: remove
            ' the gen tab and switch back to this tab. The
            ' difference is that onSuccess also kicks a refresh.
            Dim teardown As Action =
                Sub()
                    Try
                        If hostTabs.TabPages.Contains(genTab) Then
                            hostTabs.TabPages.Remove(genTab)
                        End If
                        If hostTabs.TabPages.Contains(mySourceTab) Then
                            hostTabs.SelectedTab = mySourceTab
                        End If
                    Catch
                    End Try
                    genTab.Dispose()
                End Sub

            Dim onClose As Action = Sub() teardown()

            Dim onSuccess As Action =
                Sub()
                    teardown()
                    If Not Me.IsDisposed Then
                        ' Same path the Refresh button uses, with a
                        ' fresh CTS so a stale in-flight refresh
                        ' doesn't clobber the post-generation listing.
                        RefreshClicked()
                    End If
                End Sub

            Dim panel As New FileGenerationPanel(_instanceId, info.TabTitle, onClose, onSuccess) With {
                .Dock = DockStyle.Fill
            }
            genTab.Controls.Add(panel)
            hostTabs.TabPages.Add(genTab)
            hostTabs.SelectedTab = genTab
        End Sub

        ' ====================================================
        '  Helpers
        ' ====================================================

        Private Sub SetStatus(text As String, color As Color)
            If Me.IsDisposed OrElse _statusLabel Is Nothing Then Return
            _statusLabel.Text = text
            _statusLabel.ForeColor = color
        End Sub

        ''' <summary>
        ''' Builds an OpenFileDialog/SaveFileDialog filter string
        ''' from the directory's AllowedExtensions list. Returns a
        ''' generic "all files" filter when the directory has no
        ''' extension restriction.
        ''' </summary>
        Private Function BuildFileFilter() As String
            If _directory.AllowedExtensions Is Nothing OrElse _directory.AllowedExtensions.Count = 0 Then
                Return "All files (*.*)|*.*"
            End If
            Dim parts = _directory.AllowedExtensions.Select(
                Function(ext) "*" & If(ext.StartsWith("."), ext, "." & ext)).ToList()
            Dim joined = String.Join(";", parts)
            Return $"{_directory.DisplayName} files ({joined})|{joined}|All files (*.*)|*.*"
        End Function

        Private Shared Function ShortName(relativePath As String) As String
            If String.IsNullOrEmpty(relativePath) Then Return ""
            Dim slashIdx = relativePath.LastIndexOfAny(New Char() {"/"c, "\"c})
            If slashIdx < 0 Then Return relativePath
            Return relativePath.Substring(slashIdx + 1)
        End Function

        Private Shared Function FormatSize(bytes As Long) As String
            If bytes < 1024 Then Return $"{bytes} B"
            If bytes < 1024L * 1024L Then Return $"{(bytes / 1024.0):F1} KB"
            If bytes < 1024L * 1024L * 1024L Then Return $"{(bytes / 1048576.0):F1} MB"
            Return $"{(bytes / 1073741824.0):F2} GB"
        End Function

        ''' <summary>
        ''' Suggests an unused filename for duplicating `oldName`.
        ''' Tries "<base> - Copy<ext>" first; if that's already in
        ''' the listview, walks numbered suffixes ("<base> - Copy (2)<ext>",
        ''' (3), …) until it finds one that isn't taken. The 99-cap
        ''' fallback uses DateTime.Ticks so the suggestion is
        ''' guaranteed unique even in pathological cases (the user
        ''' will never hit that branch in practice; saves directories
        ''' don't accumulate 99 "Copy (n)" variants).
        ''' </summary>
        Private Function SuggestDuplicateName(oldName As String) As String
            Dim baseName = Path.GetFileNameWithoutExtension(oldName)
            Dim ext = Path.GetExtension(oldName)

            Dim existing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each item As ListViewItem In _listView.Items
                existing.Add(item.Text)
            Next

            Dim candidate = $"{baseName} - Copy{ext}"
            If Not existing.Contains(candidate) Then Return candidate

            For i = 2 To 99
                candidate = $"{baseName} - Copy ({i}){ext}"
                If Not existing.Contains(candidate) Then Return candidate
            Next

            ' Pathological fallback — 99+ copies. Ticks is unique
            ' enough that even a fast-fingered user won't collide.
            Return $"{baseName} - Copy ({DateTime.UtcNow.Ticks}){ext}"
        End Function

        ' ====================================================
        '  Node resolution
        ' ====================================================

        ''' <summary>
        ''' Bundle of (client, installPath) returned by ResolveNode
        ''' so callers don't have to make two separate lookups.
        ''' Returns Nothing as a sentinel for "couldn't resolve" —
        ''' callers display a friendly status message rather than
        ''' propagating the failure.
        ''' </summary>
        Private Class ResolvedNode
            Public Client As INodeClient
            Public InstallPath As String
        End Class

        Private Function ResolveNode() As ResolvedNode
            Try
                Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
                If factory Is Nothing Then Return Nothing
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then Return Nothing
                    Dim installEntity = db.Installations.Find(instanceEntity.InstallationId)
                    If installEntity Is Nothing Then Return Nothing
                    Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                    If nodeEntity Is Nothing Then Return Nothing
                    Dim client = factory.GetClient(nodeEntity.NodeId,
                                                    nodeEntity.HostAddress,
                                                    nodeEntity.Port,
                                                    nodeEntity.AuthToken)
                    Return New ResolvedNode With {
                        .Client = client,
                        .InstallPath = installEntity.InstallPath
                    }
                End Using
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
