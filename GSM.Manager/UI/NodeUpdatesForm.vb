Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Plugin

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 8-2 slice 7 — Nodes → Update Nodes. Lists every configured
    ''' node with its current build + reachability, and pushes a binary to
    ''' the selected nodes via the chunked staged-binary endpoint, then
    ''' triggers the node's update-exit (detach shims → a survivor swaps
    ''' .new over the live binary → relaunch). Each node is handled
    ''' INDEPENDENTLY: an unreachable, detached, or failing node never
    ''' blocks the others — multi-node fleets routinely have some node
    ''' that's offline, mid-session, or simply not one the operator wants
    ''' to touch yet, so node updates are deliberately decoupled from the
    ''' Manager's own self-update (Help → Check for updates) and from each
    ''' other.
    '''
    ''' Two sourcing modes ship here:
    '''   • Manual push — the operator picks a local binary file (a release
    '''     build or one they built themselves). They own the versioning
    '''     and the consequences of what they push; the node verifies
    '''     SHA-256 + size on commit and the survivor relaunch is the
    '''     integrity backstop.
    '''   • Feed-driven (7-source-b) — tick "Latest release" and the per-
    '''     platform binary is downloaded from the GitHub release feed,
    '''     SHA-256 verified against the release SHA256SUMS, and pushed —
    '''     no file picking. The Latest column (7-source-a) shows the newest
    '''     release and tints nodes behind it.
    '''
    ''' The Target selector carries Node / Shim / NodeSetup. Node swaps the
    ''' live binary via a survivor and relaunches (brief offline); Shim
    ''' installs a side-by-side version folder and NodeSetup swaps its idle
    ''' binary in place — neither bounces the node. All three source from the
    ''' same node zip (manual pick or feed).
    ''' </summary>
    Public Class NodeUpdatesForm
        Inherits Form

        Private ReadOnly _factory As NodeHttpClientFactory
        Private ReadOnly _source As NodeReleaseSource

        Private _grid As ListView
        Private _targetCombo As ComboBox
        Private _versionBox As TextBox
        Private _statusLabel As Label
        Private _updateButton As Button
        Private _recheckButton As Button
        Private _selectAll As CheckBox
        Private _feedCheck As CheckBox
        Private _suppressCheckEvents As Boolean
        Private _busy As Boolean

        ' Wire target name for the staged-binary push: "node" / "shim" / "nodesetup".
        Private _target As String = "node"

        ' Newest release from the GitHub feed (7-source-a): version drives the
        ' Latest column + per-node compare; tag drives feed sourcing (7-source-b).
        Private _latestVersion As String = ""
        Private _latestTag As String = ""
        Private Shared ReadOnly UpdateAvailableColor As Color = Color.FromArgb(255, 248, 209)

        ' Per-row model carried in ListViewItem.Tag.
        Private Class NodeRow
            Public Property NodeId As String
            Public Property Display As String
            Public Property Host As String
            Public Property Port As Integer
            Public Property AuthToken As String
            Public Property Detached As Boolean
            Public Property Reachable As Boolean
            Public Property Platform As NodePlatform
        End Class

        ' A picked/sourced binary + its resolved version, mapped per platform.
        Private Class BinaryPick
            Public Property Path As String
            Public Property Version As String
        End Class

        Public Sub New()
            _factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
            _source = ManagerProgram.Services.GetService(Of NodeReleaseSource)()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Load, AddressOf OnDialogLoad
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Update Nodes"
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(740, 420)
            Me.MinimumSize = New Size(640, 320)

            Dim targetLabel As New Label With {
                .Text = "Target:", .Location = New Point(12, 14), .AutoSize = True}
            Me.Controls.Add(targetLabel)

            ' Node / Shim / NodeSetup — all three wired (OnTargetChanged sets
            ' _target). Node bounces the node; shim/nodesetup keep it up.
            _targetCombo = New ComboBox With {
                .Location = New Point(60, 10), .Size = New Size(150, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList}
            _targetCombo.Items.AddRange(New Object() {"Node", "Shim", "NodeSetup"})
            _targetCombo.SelectedIndex = 0
            AddHandler _targetCombo.SelectedIndexChanged, AddressOf OnTargetChanged
            Me.Controls.Add(_targetCombo)

            Dim versionLabel As New Label With {
                .Text = "Version:", .Location = New Point(228, 14), .AutoSize = True}
            Me.Controls.Add(versionLabel)

            ' Optional, manual-mode only. Prefilled from the file's
            ' ProductVersion on push when blank. Disabled in feed mode (the
            ' release tag is authoritative there).
            _versionBox = New TextBox With {
                .Location = New Point(282, 10), .Size = New Size(140, 24)}
            Me.Controls.Add(_versionBox)

            ' Feed mode: download the per-platform binary from the release
            ' feed instead of picking a file. Disabled when feed sourcing is
            ' unavailable (no NodeReleaseSource registered).
            _feedCheck = New CheckBox With {
                .Text = "Latest release", .Location = New Point(528, 12), .AutoSize = True,
                .Enabled = (_source IsNot Nothing)}
            AddHandler _feedCheck.CheckedChanged, AddressOf OnFeedModeChanged
            Me.Controls.Add(_feedCheck)

            _selectAll = New CheckBox With {
                .Text = "Select all", .Location = New Point(440, 12), .AutoSize = True}
            AddHandler _selectAll.CheckedChanged, AddressOf OnSelectAllChanged
            Me.Controls.Add(_selectAll)

            _grid = New ListView With {
                .View = View.Details, .FullRowSelect = True, .GridLines = True,
                .MultiSelect = True, .HideSelection = False, .CheckBoxes = True,
                .Location = New Point(12, 44), .Size = New Size(704, 286),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right}
            _grid.Columns.Add("Node", 150)
            _grid.Columns.Add("Address", 135)
            _grid.Columns.Add("Installed", 85)
            _grid.Columns.Add("Latest", 100)
            _grid.Columns.Add("Platform", 70)
            _grid.Columns.Add("Status", 85)
            _grid.Columns.Add("Result", 140)
            AddHandler _grid.ItemCheck, AddressOf OnItemCheck
            AddHandler _grid.ItemChecked, Sub(s, e) OnItemChecked()
            Me.Controls.Add(_grid)

            _statusLabel = New Label With {
                .Location = New Point(12, 346), .Size = New Size(360, 20),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right,
                .AutoEllipsis = True, .ForeColor = SystemColors.GrayText, .Text = ""}
            Me.Controls.Add(_statusLabel)

            _recheckButton = New Button With {
                .Text = "Re-check", .Location = New Point(434, 342), .Size = New Size(90, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .Enabled = False}
            AddHandler _recheckButton.Click, Sub(s, e) LoadNodes(True)
            Me.Controls.Add(_recheckButton)

            _updateButton = New Button With {
                .Text = "Update...", .Location = New Point(530, 342), .Size = New Size(120, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .Enabled = False}
            AddHandler _updateButton.Click, Sub(s, e) OnUpdate()
            Me.Controls.Add(_updateButton)

            Dim closeButton As New Button With {
                .Text = "Close", .Location = New Point(656, 342), .Size = New Size(60, 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right, .DialogResult = DialogResult.OK}
            Me.Controls.Add(closeButton)
            Me.CancelButton = closeButton
        End Sub

        Private Sub OnDialogLoad(sender As Object, e As EventArgs)
            LoadNodes(False)
        End Sub

        ''' <summary>
        ''' Map the Target selection to the wire target name. Node swaps +
        ''' relaunches; shim installs a versioned folder; nodesetup swaps its
        ''' idle binary in place.
        ''' </summary>
        Private Sub OnTargetChanged(sender As Object, e As EventArgs)
            Select Case _targetCombo.SelectedIndex
                Case 1
                    _target = "shim"
                Case 2
                    _target = "nodesetup"
                Case Else
                    _target = "node"
            End Select
            _statusLabel.Text = $"Target: {_targetCombo.Text}"
        End Sub

        ''' <summary>Base file name of the current target's binary (no extension).</summary>
        Private Function TargetBaseName() As String
            Select Case _target
                Case "shim"
                    Return "GSM.Shim"
                Case "nodesetup"
                    Return "GSM.NodeSetup"
                Case Else
                    Return "GSM.Node"
            End Select
        End Function

        ''' <summary>
        ''' One-line description of what applying the current target does to a
        ''' node, for the confirm prompts. Only "node" bounces the node.
        ''' </summary>
        Private Function ApplyEffectNote() As String
            Select Case _target
                Case "shim"
                    Return "The shim installs as a new side-by-side version; running instances aren't affected and the node stays up."
                Case "nodesetup"
                    Return "NodeSetup's idle binary is swapped in place; the node stays up."
                Case Else
                    Return "Each node then swaps and relaunches — it will briefly go offline."
            End Select
        End Function

        Private Function FeedModeOn() As Boolean
            Return _feedCheck IsNot Nothing AndAlso _feedCheck.Checked
        End Function

        Private Sub OnFeedModeChanged(sender As Object, e As EventArgs)
            _versionBox.Enabled = Not FeedModeOn()
            UpdateButtons()
        End Sub

        ' Veto checking rows that can't receive a push (unreachable).
        Private Sub OnItemCheck(sender As Object, e As ItemCheckEventArgs)
            If _suppressCheckEvents Then Return
            If e.NewValue = CheckState.Checked Then
                Dim row = TryCast(_grid.Items(e.Index).Tag, NodeRow)
                If row Is Nothing OrElse Not row.Reachable Then
                    e.NewValue = CheckState.Unchecked
                End If
            End If
        End Sub

        Private Function CheckedRows() As List(Of NodeRow)
            Dim rows As New List(Of NodeRow)
            For Each item As ListViewItem In _grid.CheckedItems
                Dim r = TryCast(item.Tag, NodeRow)
                If r IsNot Nothing Then rows.Add(r)
            Next
            Return rows
        End Function

        Private Sub OnSelectAllChanged(sender As Object, e As EventArgs)
            If _suppressCheckEvents Then Return
            _suppressCheckEvents = True
            Try
                For Each item As ListViewItem In _grid.Items
                    Dim r = TryCast(item.Tag, NodeRow)
                    item.Checked = (_selectAll.Checked AndAlso r IsNot Nothing AndAlso r.Reachable)
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
                Dim reachableCount As Integer = 0
                For Each item As ListViewItem In _grid.Items
                    Dim r = TryCast(item.Tag, NodeRow)
                    If r IsNot Nothing AndAlso r.Reachable Then reachableCount += 1
                Next
                _selectAll.Checked = reachableCount > 0 AndAlso _grid.CheckedItems.Count = reachableCount
            Finally
                _suppressCheckEvents = False
            End Try
            UpdateButtons()
        End Sub

        Private Sub UpdateButtons()
            If _busy Then Return
            Dim count = _grid.CheckedItems.Count
            _updateButton.Enabled = count > 0
            If FeedModeOn() Then
                _updateButton.Text = If(count > 1, $"Update {count} to latest…", "Update to latest…")
            Else
                _updateButton.Text = If(count > 1, $"Update ({count})...", "Update...")
            End If
        End Sub

        ''' <summary>
        ''' Centralised enable/disable for the whole dialog while a push (or
        ''' a feed download) is in flight. Respects feed-mode for the version
        ''' box and the no-source case for the feed checkbox on restore.
        ''' </summary>
        Private Sub SetControlsBusy(busy As Boolean)
            _busy = busy
            _targetCombo.Enabled = Not busy
            _versionBox.Enabled = (Not busy) AndAlso Not FeedModeOn()
            _selectAll.Enabled = Not busy
            _grid.Enabled = Not busy
            _recheckButton.Enabled = Not busy
            If _feedCheck IsNot Nothing Then _feedCheck.Enabled = (Not busy) AndAlso (_source IsNot Nothing)
            If busy Then
                _updateButton.Enabled = False
            Else
                UpdateButtons()
            End If
        End Sub

        ''' <summary>
        ''' (Re)load the node list and probe each node's current build +
        ''' reachability. Probes run concurrently, each bounded ~8s so one
        ''' unreachable node doesn't hold the whole list at the client's
        ''' 30s timeout.
        ''' </summary>
        Private Async Sub LoadNodes(forceRefresh As Boolean)
            If _factory Is Nothing Then
                _statusLabel.Text = "Node client factory unavailable."
                Return
            End If
            _busy = True
            _grid.Items.Clear()
            _suppressCheckEvents = True
            Try
                _selectAll.Checked = False
            Finally
                _suppressCheckEvents = False
            End Try
            _recheckButton.Enabled = False
            _updateButton.Enabled = False
            _statusLabel.Text = "Loading nodes…"

            ' Snapshot node rows (id/host/port/auth/detached) off the DB.
            Dim rows As New List(Of NodeRow)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    For Each n In db.Nodes.OrderBy(Function(x) x.DisplayName).ToList()
                        rows.Add(New NodeRow With {
                            .NodeId = n.NodeId, .Display = n.DisplayName,
                            .Host = n.HostAddress, .Port = n.Port, .AuthToken = n.AuthToken,
                            .Detached = Not n.IsEnabled, .Reachable = False})
                    Next
                End Using
            Catch ex As Exception
                _statusLabel.Text = $"Failed to load nodes: {ex.Message}"
                _busy = False
                _recheckButton.Enabled = True
                Return
            End Try

            For Each r In rows
                Dim item As New ListViewItem(r.Display)
                item.SubItems.Add($"{r.Host}:{r.Port}")
                item.SubItems.Add("…")                                  ' Installed
                item.SubItems.Add("—")                                  ' Latest
                item.SubItems.Add("")                                   ' Platform
                item.SubItems.Add(If(r.Detached, "Detached", "Checking…")) ' Status
                item.SubItems.Add("")                                   ' Result
                item.Tag = r
                _grid.Items.Add(item)
            Next

            ' Resolve the newest release once (7-source-a) so each probe can
            ' flag whether its node is behind it. Best-effort — if the feed is
            ' unreachable the Latest column just shows "—".
            _statusLabel.Text = "Checking latest release…"
            _latestTag = ""
            _latestVersion = Await ResolveLatestVersionAsync()

            Dim probes As New List(Of Task)
            For i = 0 To _grid.Items.Count - 1
                probes.Add(ProbeVersionAsync(_grid.Items(i), forceRefresh))
            Next
            Try
                Await Task.WhenAll(probes)
            Catch
                ' Per-row handler records failures; aggregate ignored.
            End Try

            _busy = False
            _recheckButton.Enabled = True
            UpdateButtons()

            Dim reachableCount As Integer = 0
            For Each item As ListViewItem In _grid.Items
                Dim r = TryCast(item.Tag, NodeRow)
                If r IsNot Nothing AndAlso r.Reachable Then reachableCount += 1
            Next
            Dim latestNote = If(String.IsNullOrEmpty(_latestVersion), "", $" · latest release {_latestVersion}")
            _statusLabel.Text = If(_grid.Items.Count = 0, "No nodes configured.",
                $"{_grid.Items.Count} node(s), {reachableCount} reachable.{latestNote}")
        End Sub

        Private Async Function ProbeVersionAsync(item As ListViewItem, force As Boolean) As Task
            Dim row = TryCast(item.Tag, NodeRow)
            If row Is Nothing Then Return
            Dim client = TryCast(_factory.GetClient(row.NodeId, row.Host, row.Port, row.AuthToken), NodeHttpClient)
            If client Is Nothing Then
                SetRowStatus(item, row, False, "—", "", If(row.Detached, "Detached", "Unavailable"))
                Return
            End If
            Try
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim ver = Await client.GetApiVersionAsync(force, cts.Token)
                    Dim build = ""
                    Dim plat = ""
                    If ver IsNot Nothing Then
                        build = If(String.IsNullOrEmpty(ver.Build), ver.Version, ver.Build)
                        plat = ver.Platform.ToString()
                        row.Platform = ver.Platform
                    End If
                    SetRowStatus(item, row, True,
                                 If(String.IsNullOrEmpty(build), "(unknown)", build), plat,
                                 If(row.Detached, "Detached", "Reachable"))
                    ApplyLatest(item, build)
                End Using
            Catch ex As Exception
                SetRowStatus(item, row, False, "—", "", If(row.Detached, "Detached", "Unreachable"))
            End Try
        End Function

        Private Sub SetRowStatus(item As ListViewItem, row As NodeRow, reachable As Boolean,
                                 installed As String, platform As String, status As String)
            row.Reachable = reachable
            item.SubItems(2).Text = installed
            item.SubItems(4).Text = platform
            item.SubItems(5).Text = status
            item.ForeColor = If(reachable, SystemColors.WindowText, SystemColors.GrayText)
        End Sub

        Private Sub OnUpdate()
            If FeedModeOn() Then
                OnUpdateFromFeed()
            Else
                OnUpdateManual()
            End If
        End Sub

        ''' <summary>
        ''' Manual push: pop one file selector per platform present in the
        ''' selection, then stage → apply → relaunch each node in turn. A
        ''' mixed-platform selection gets a binary it can actually run for
        ''' each OS; one node's failure never aborts the batch.
        ''' </summary>
        Private Async Sub OnUpdateManual()
            Dim selected = CheckedRows()
            If selected.Count = 0 Then Return

            ' Distinct KNOWN platforms among the checked nodes (checked rows
            ' are reachable, so most have reported their OS; a pre-5f-1 node
            ' may still be Unknown).
            Dim knownPlatforms As New List(Of NodePlatform)
            For Each r In selected
                If r.Platform <> NodePlatform.Unknown AndAlso Not knownPlatforms.Contains(r.Platform) Then
                    knownPlatforms.Add(r.Platform)
                End If
            Next

            ' Gather one binary per platform. A mixed selection pops a
            ' selector each (Linux binary AND Windows binary) so the whole
            ' selection goes in one pass. If every selected node is
            ' Unknown-platform, fall back to a single file pushed to all
            ' (can't match what we can't identify).
            Dim picks As New Dictionary(Of NodePlatform, BinaryPick)
            Dim wildcard As BinaryPick = Nothing
            If knownPlatforms.Count = 0 Then
                wildcard = PickBinaryForPlatform(NodePlatform.Unknown)
                If wildcard Is Nothing Then Return
            Else
                For Each plat In knownPlatforms
                    Dim pick = PickBinaryForPlatform(plat)
                    If pick Is Nothing Then Return   ' cancelled -> abort the whole op
                    picks(plat) = pick
                Next
            End If

            ' Map each node to the binary it should receive.
            Dim plan As New List(Of KeyValuePair(Of NodeRow, BinaryPick))
            Dim skipped As New List(Of NodeRow)
            For Each r In selected
                If r.Platform <> NodePlatform.Unknown AndAlso picks.ContainsKey(r.Platform) Then
                    plan.Add(New KeyValuePair(Of NodeRow, BinaryPick)(r, picks(r.Platform)))
                ElseIf wildcard IsNot Nothing Then
                    plan.Add(New KeyValuePair(Of NodeRow, BinaryPick)(r, wildcard))
                Else
                    skipped.Add(r)   ' Unknown-platform node in a typed push
                End If
            Next

            If plan.Count = 0 Then
                MessageBox.Show(Me, "None of the selected nodes could be matched to a binary.",
                                "Update Nodes", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' Confirm — one line per binary with its node count.
            Dim lines As New List(Of String)
            If wildcard IsNot Nothing Then
                lines.Add($"• {Path.GetFileName(wildcard.Path)}{VersionNote(wildcard.Version)} → {plan.Count} node(s)")
            Else
                For Each plat In knownPlatforms
                    Dim p = picks(plat)
                    Dim cnt = 0
                    For Each kv In plan
                        If kv.Value Is p Then cnt += 1
                    Next
                    lines.Add($"• {PlatformLabel(plat)}: {Path.GetFileName(p.Path)}{VersionNote(p.Version)} → {cnt} node(s)")
                Next
            End If
            Dim prompt = $"Push to {plan.Count} node(s) and apply?" & Environment.NewLine & Environment.NewLine &
                         String.Join(Environment.NewLine, lines)
            If skipped.Count > 0 Then
                prompt &= Environment.NewLine & Environment.NewLine &
                          $"({skipped.Count} node(s) skipped — platform unknown, can't match a binary.)"
            End If
            prompt &= Environment.NewLine & Environment.NewLine &
                      "Each node verifies the upload (SHA-256 + size). " & ApplyEffectNote() &
                      " You are responsible for the binaries you push."
            If MessageBox.Show(Me, prompt, "Update Nodes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then
                Return
            End If

            For Each r In skipped
                SetResult(FindItem(r), "Skipped: node platform unknown")
            Next

            Await RunStagingAsync(plan)
        End Sub

        ''' <summary>
        ''' Feed push (7-source-b): download the per-platform node binary
        ''' from the GitHub release feed at the latest tag, SHA-256 verify it
        ''' against the release SHA256SUMS, then run the same stage → apply →
        ''' relaunch loop the manual path uses. One download per platform is
        ''' shared across same-platform nodes (NodeReleaseSource caches).
        ''' Unknown-platform nodes can't be matched to a release asset and are
        ''' skipped.
        ''' </summary>
        Private Async Sub OnUpdateFromFeed()
            Dim selected = CheckedRows()
            If selected.Count = 0 Then Return
            If _source Is Nothing Then
                MessageBox.Show(Me, "Release-feed sourcing isn't available.",
                                "Update Nodes", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            If String.IsNullOrEmpty(_latestTag) Then
                MessageBox.Show(Me, "No latest release has been resolved yet — try Re-check.",
                                "Update Nodes", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim knownPlatforms As New List(Of NodePlatform)
            Dim skipped As New List(Of NodeRow)
            For Each r In selected
                If r.Platform = NodePlatform.Unknown Then
                    skipped.Add(r)
                ElseIf Not knownPlatforms.Contains(r.Platform) Then
                    knownPlatforms.Add(r.Platform)
                End If
            Next

            If knownPlatforms.Count = 0 Then
                MessageBox.Show(Me,
                    "The selected node(s) report an unknown platform, so the feed can't pick a binary for them.",
                    "Update Nodes", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' Confirm — per-platform release binary + node count.
            Dim lines As New List(Of String)
            For Each plat In knownPlatforms
                Dim cnt = selected.Where(Function(r) r.Platform = plat).Count()
                Dim baseName = TargetBaseName()
                Dim binName = If(plat = NodePlatform.Windows, baseName & ".exe", baseName)
                lines.Add($"• {PlatformLabel(plat)}: {binName} v{_latestVersion} → {cnt} node(s)")
            Next
            Dim targetCount = selected.Count - skipped.Count
            Dim prompt = $"Download the latest release ({_latestVersion}) and push to {targetCount} node(s)?" &
                         Environment.NewLine & Environment.NewLine & String.Join(Environment.NewLine, lines)
            If skipped.Count > 0 Then
                prompt &= Environment.NewLine & Environment.NewLine &
                          $"({skipped.Count} node(s) skipped — platform unknown, the feed can't match a binary.)"
            End If
            prompt &= Environment.NewLine & Environment.NewLine &
                      "Each binary is SHA-256 verified against the release before it's pushed, and nodes verify again on commit. " &
                      ApplyEffectNote()
            If MessageBox.Show(Me, prompt, "Update Nodes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then
                Return
            End If

            For Each r In skipped
                SetResult(FindItem(r), "Skipped: platform unknown (feed)")
            Next

            ' Source one verified binary per platform (cached across nodes).
            SetControlsBusy(True)
            Dim picks As New Dictionary(Of NodePlatform, BinaryPick)
            Dim sourceErrors As New Dictionary(Of NodePlatform, String)
            For Each plat In knownPlatforms
                Dim capturedPlat = plat
                _statusLabel.Text = $"Downloading {PlatformLabel(capturedPlat)} node {_latestVersion}…"
                Dim prog As New Progress(Of StageProgress)(
                    Sub(sp) _statusLabel.Text = $"{PlatformLabel(capturedPlat)} node: {sp.Phase}…")
                Dim res As NodeSourceResult = Nothing
                Try
                    res = Await _source.SourceAsync(plat, _latestTag, _target, prog, CancellationToken.None)
                Catch ex As Exception
                    res = New NodeSourceResult With {.Success = False, .ErrorMessage = ex.Message}
                End Try
                If res IsNot Nothing AndAlso res.Success Then
                    picks(plat) = New BinaryPick With {.Path = res.BinaryPath, .Version = res.Version}
                Else
                    sourceErrors(plat) = If(res?.ErrorMessage, "sourcing failed")
                End If
            Next

            ' Build the plan; nodes whose platform failed to source are flagged.
            Dim plan As New List(Of KeyValuePair(Of NodeRow, BinaryPick))
            For Each r In selected
                If r.Platform = NodePlatform.Unknown Then Continue For
                If picks.ContainsKey(r.Platform) Then
                    plan.Add(New KeyValuePair(Of NodeRow, BinaryPick)(r, picks(r.Platform)))
                Else
                    SetResult(FindItem(r), $"Failed: {sourceErrors.GetValueOrDefault(r.Platform, "source error")}")
                End If
            Next

            If plan.Count = 0 Then
                SetControlsBusy(False)
                _statusLabel.Text = "Nothing pushed — no binary could be sourced."
                Return
            End If

            Await RunStagingAsync(plan)
        End Sub

        ''' <summary>
        ''' Push every (node, binary) pair in the plan sequentially:
        ''' stage → apply → poll for relaunch. One node's failure never blocks
        ''' the rest. Shared by the manual and feed paths.
        ''' </summary>
        Private Async Function RunStagingAsync(plan As List(Of KeyValuePair(Of NodeRow, BinaryPick))) As Task
            SetControlsBusy(True)

            Dim okCount = 0
            Dim failCount = 0
            For Each kv In plan
                Dim r = kv.Key
                Dim pick = kv.Value
                Dim item = FindItem(r)
                SetResult(item, "Staging…")
                Dim client = TryCast(_factory.GetClient(r.NodeId, r.Host, r.Port, r.AuthToken), NodeHttpClient)
                If client Is Nothing Then
                    SetResult(item, "Failed: no client")
                    failCount += 1
                    Continue For
                End If
                Try
                    ' Shim version folders are named by pure version (the node
                    ' runs Version.TryParse on the folder), so drop any +sha build
                    ' metadata before staging a shim.
                    Dim ver = pick.Version
                    If _target = "shim" AndAlso Not String.IsNullOrEmpty(ver) Then
                        Dim plus = ver.IndexOf("+"c)
                        If plus >= 0 Then ver = ver.Substring(0, plus)
                    End If
                    Await client.StageBinaryAsync(_target, pick.Path, ver, CancellationToken.None)
                    SetResult(item, "Applying…")
                    Await client.ApplyUpdateAsync(_target, CancellationToken.None)
                    If _target = "node" Then
                        SetResult(item, "Relaunching…")
                        Dim back = Await WaitForNodeBackAsync(client, TimeSpan.FromSeconds(60))
                        If back IsNot Nothing Then
                            SetResult(item, $"Updated → {back}")
                        Else
                            SetResult(item, "Applied; node not back yet")
                        End If
                    Else
                        ' shim / nodesetup: commit + apply already installed it and
                        ' the node stays up — nothing to wait for.
                        SetResult(item, "Installed")
                    End If
                    okCount += 1
                Catch ex As Exception
                    SetResult(item, $"Failed: {ex.Message}")
                    failCount += 1
                End Try
            Next

            SetControlsBusy(False)
            _statusLabel.Text = $"Done — {okCount} updated, {failCount} failed."
        End Function

        ''' <summary>
        ''' Pop a file selector for a specific platform's node binary,
        ''' validate the picked file's format (magic bytes) matches, and
        ''' resolve its version (the Version box if set, else the file's
        ''' ProductVersion). Loops on a platform mismatch so a wrong pick
        ''' doesn't abort the batch; returns Nothing if the operator cancels.
        ''' </summary>
        Private Function PickBinaryForPlatform(plat As NodePlatform) As BinaryPick
            Do
                Dim picked As String
                Using ofd As New OpenFileDialog()
                    ofd.CheckFileExists = True
                    Dim baseName = TargetBaseName()
                    Dim lbl = _target
                    Select Case plat
                        Case NodePlatform.Linux
                            ofd.Title = $"Select the Linux {lbl} binary ({baseName})"
                            ofd.Filter = $"Linux {lbl} binary ({baseName})|{baseName}|All files (*.*)|*.*"
                        Case NodePlatform.Windows
                            ofd.Title = $"Select the Windows {lbl} binary ({baseName}.exe)"
                            ofd.Filter = $"Windows {lbl} binary ({baseName}.exe)|{baseName}.exe|All files (*.*)|*.*"
                        Case Else
                            ofd.Title = $"Select the {lbl} binary to push"
                            ofd.Filter = $"{lbl} binary ({baseName}*)|{baseName};{baseName}.exe|All files (*.*)|*.*"
                    End Select
                    If ofd.ShowDialog(Me) <> DialogResult.OK Then Return Nothing
                    picked = ofd.FileName
                End Using

                Dim detected = DetectBinaryPlatform(picked)
                If plat <> NodePlatform.Unknown AndAlso detected <> NodePlatform.Unknown AndAlso detected <> plat Then
                    If MessageBox.Show(Me,
                        $"That's a {PlatformLabel(detected)} binary, but a {PlatformLabel(plat)} binary is needed here. Choose a different file?",
                        "Update Nodes", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) = DialogResult.Retry Then
                        Continue Do
                    End If
                    Return Nothing
                End If
                If detected = NodePlatform.Unknown Then
                    If MessageBox.Show(Me,
                        "This file isn't a recognizable Windows (PE) or Linux (ELF) executable. Use it anyway?",
                        "Update Nodes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then
                        Return Nothing
                    End If
                End If

                Dim ver = _versionBox.Text.Trim()
                If String.IsNullOrEmpty(ver) Then
                    Try
                        Dim fvi = FileVersionInfo.GetVersionInfo(picked)
                        ver = If(fvi IsNot Nothing AndAlso fvi.ProductVersion IsNot Nothing, fvi.ProductVersion.Trim(), "")
                    Catch
                        ver = ""
                    End Try
                End If

                ' Shim binaries live in GSM.Shim\<version>\ and (on Linux) are ELF
                ' with no PE version resource, so ProductVersion is blank — fall
                ' back to the version folder name the publish drops them into.
                If _target = "shim" AndAlso String.IsNullOrEmpty(ver) Then
                    Dim parent As String = ""
                    Try
                        parent = Path.GetFileName(Path.GetDirectoryName(picked))
                    Catch
                    End Try
                    Dim parsed As Version = Nothing
                    If Not String.IsNullOrEmpty(parent) AndAlso Version.TryParse(parent, parsed) Then
                        ver = parent
                    End If
                End If

                ' A shim push with no resolvable version would be rejected by the
                ' node (400); tell the operator up front instead.
                If _target = "shim" AndAlso String.IsNullOrEmpty(ver) Then
                    MessageBox.Show(Me,
                        "A shim push needs a version. Type it in the Version box (e.g. 0.3.0), or pick the " &
                        "binary from its GSM.Shim\<version>\ folder so the version can be read from the folder name.",
                        "Update Nodes", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return Nothing
                End If

                Return New BinaryPick With {.Path = picked, .Version = ver}
            Loop
        End Function

        Private Shared Function VersionNote(version As String) As String
            Return If(String.IsNullOrEmpty(version), "", $" v{version}")
        End Function

        ''' <summary>
        ''' Poll /api/version (forced past the cache) until the node answers
        ''' again or the timeout elapses. Returns the build it came back on,
        ''' or Nothing on timeout. Connection errors while the node is
        ''' tearing down / relaunching are expected and simply retried.
        ''' (Health-gate + rollback on a bad relaunch is slice 8.)
        ''' </summary>
        Private Async Function WaitForNodeBackAsync(client As NodeHttpClient, timeout As TimeSpan) As Task(Of String)
            Dim deadline = DateTime.UtcNow + timeout
            ' Give the node a moment to actually tear down before polling.
            Await Task.Delay(TimeSpan.FromSeconds(2))
            While DateTime.UtcNow < deadline
                Try
                    Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(5))
                        Dim ver = Await client.GetApiVersionAsync(True, cts.Token)
                        If ver IsNot Nothing Then
                            Return If(String.IsNullOrEmpty(ver.Build), ver.Version, ver.Build)
                        End If
                    End Using
                Catch
                    ' Node still down / restarting — keep waiting.
                End Try
                Await Task.Delay(TimeSpan.FromSeconds(2))
            End While
            Return Nothing
        End Function

        ''' <summary>
        ''' Resolve the newest release from the GitHub feed for the Latest
        ''' column (version) and feed sourcing (tag). Uses the background
        ''' checker's last persisted result (instant); only does a bounded
        ''' live check if nothing is cached yet. Sets _latestTag as a side
        ''' effect. Returns "" on any failure so the column degrades to "—".
        ''' </summary>
        Private Async Function ResolveLatestVersionAsync() As Task(Of String)
            Try
                Dim checker = ManagerProgram.Services.GetService(Of GitHubReleaseChecker)()
                If checker Is Nothing Then Return ""
                Dim status = checker.GetPersistedStatus()
                Dim cached = PickVersionString(status)
                If Not String.IsNullOrEmpty(cached) Then
                    _latestTag = PickTagString(status)
                    Return cached
                End If
                ' Nothing cached yet (fresh Manager start) — one bounded check.
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim live = Await checker.CheckNowAsync(cts.Token)
                    _latestTag = PickTagString(live)
                    Return PickVersionString(live)
                End Using
            Catch
                Return ""
            End Try
        End Function

        Private Shared Function PickVersionString(status As UpdateStatus) As String
            If status Is Nothing Then Return ""
            Dim v = If(Not String.IsNullOrEmpty(status.LatestVersion), status.LatestVersion, If(status.LatestTag, ""))
            If v.StartsWith("v", StringComparison.OrdinalIgnoreCase) Then v = v.Substring(1)
            Return v
        End Function

        ''' <summary>
        ''' Resolve the raw release TAG (what the GitHub API path needs).
        ''' Prefers LatestTag; falls back to "v"+LatestVersion so a feed that
        ''' only surfaces a version still yields a usable tag.
        ''' </summary>
        Private Shared Function PickTagString(status As UpdateStatus) As String
            If status Is Nothing Then Return ""
            Dim t = If(status.LatestTag, "")
            If String.IsNullOrEmpty(t) AndAlso Not String.IsNullOrEmpty(status.LatestVersion) Then
                t = If(status.LatestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase),
                       status.LatestVersion, "v" & status.LatestVersion)
            End If
            Return t
        End Function

        ''' <summary>
        ''' Fill the Latest column for a reachable node and flag it when the
        ''' installed build is behind the newest release (SemanticVersion
        ''' compare; the row is tinted when an update is available).
        ''' </summary>
        Private Sub ApplyLatest(item As ListViewItem, installedBuild As String)
            If item Is Nothing Then Return
            If String.IsNullOrEmpty(_latestVersion) Then
                item.SubItems(3).Text = "—"
                Return
            End If
            Dim latest = SemanticVersion.TryParse(_latestVersion)
            Dim installed = SemanticVersion.TryParse(installedBuild)
            If latest Is Nothing OrElse installed Is Nothing Then
                item.SubItems(3).Text = _latestVersion
            ElseIf latest.IsNewerThan(installed) Then
                item.SubItems(3).Text = _latestVersion & " (update)"
                item.BackColor = UpdateAvailableColor
            Else
                item.SubItems(3).Text = "current"
            End If
        End Sub

        Private Function FindItem(row As NodeRow) As ListViewItem
            For Each item As ListViewItem In _grid.Items
                If ReferenceEquals(item.Tag, row) Then Return item
            Next
            Return Nothing
        End Function

        ''' <summary>
        ''' Sniff a file's executable format from its first bytes: ELF
        ''' (0x7F 'E' 'L' 'F') -> Linux, PE/MZ ('M' 'Z') -> Windows, else
        ''' Unknown. OS-level only — architecture (x64 vs arm) isn't
        ''' distinguished here, matching what the node reports.
        ''' </summary>
        Private Shared Function DetectBinaryPlatform(path As String) As NodePlatform
            Try
                Dim head(3) As Byte
                Dim got As Integer
                Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    got = fs.Read(head, 0, 4)
                End Using
                If got >= 4 AndAlso head(0) = &H7F AndAlso head(1) = &H45 AndAlso head(2) = &H4C AndAlso head(3) = &H46 Then
                    Return NodePlatform.Linux
                End If
                If got >= 2 AndAlso head(0) = &H4D AndAlso head(1) = &H5A Then
                    Return NodePlatform.Windows
                End If
            Catch
            End Try
            Return NodePlatform.Unknown
        End Function

        Private Shared Function PlatformLabel(p As NodePlatform) As String
            Select Case p
                Case NodePlatform.Windows
                    Return "Windows"
                Case NodePlatform.Linux
                    Return "Linux"
                Case Else
                    Return "unknown-platform"
            End Select
        End Function

        Private Sub SetResult(item As ListViewItem, text As String)
            If item Is Nothing Then Return
            item.SubItems(6).Text = text
        End Sub

    End Class

End Namespace
