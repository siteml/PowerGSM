Imports System
Imports System.Diagnostics
Imports System.Drawing
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Plugin

' ============================================================
'  MainForm — primary application window
'
'  Layout:
'    Left:  TreeView (nodes → installations → instances)
'    Right: Panel that swaps content based on tree selection
'
'  Tree structure:
'    [Nodes]
'      ├─ NodeName (host:port)
'      │   ├─ [Installations]
'      │   │   ├─ InstallName (gameId)
'      │   │   │   ├─ InstanceName (state)
'      │   │   │   └─ InstanceName (state)
'      │   │   └─ InstallName (gameId)
'      │   └─ ...
'      └─ NodeName
'    [Automation Rules]
'    [Settings]
' ============================================================

Namespace GSM.Manager.UI

    Public Class MainForm
        Inherits Form

        ' ---- Controls ----
        Private WithEvents _splitContainer As SplitContainer
        Private WithEvents _treeView As TreeView
        Private _contentPanel As Panel
        Private _menuStrip As MenuStrip
        Private _statusStrip As StatusStrip
        Private _statusLabel As ToolStripStatusLabel
        Private _versionStatusLabel As ToolStripStatusLabel

        ' ---- Tree root nodes ----
        Private _nodesRoot As TreeNode
        Private _automationRoot As TreeNode
        Private _settingsRoot As TreeNode

        ' ---- Current panel ----
        Private _currentPanel As UserControl

        ' Suppress the AfterSelect handler's panel-swap side effect
        ' during programmatic SelectedNode assignments inside
        ' RefreshNodeTree. Without this, restoring selection after
        ' a tree rebuild fires AfterSelect, which calls ShowPanel,
        ' which disposes and recreates the currently-shown panel —
        ' destroying any state on it (e.g. an InstallationPanel's
        ' listview selection that was just set by OnReorderInstance).
        Private _suppressTreeAfterSelect As Boolean

        ' ---- Non-modal child windows ----
        ' Tracked so repeat clicks bring an existing window to front
        ' rather than spawning duplicates. Forms are dropped from
        ' tracking when they close.
        Private _automationWindow As AutomationRulesForm

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeComponent()
            BuildTree()
            ShowPanel(New WelcomePanel())
            AddHandler Me.Shown, AddressOf MainForm_Shown
            AddHandler Me.FormClosing, AddressOf MainForm_FormClosing
        End Sub

        Private Sub MainForm_Shown(sender As Object, e As EventArgs)
            ' Configure splitter after the form has its final size.
            ' Doing this at construction time can throw because the
            ' SplitContainer's Width isn't large enough yet to satisfy
            ' Panel1MinSize + Panel2MinSize + SplitterWidth.
            Try
                _splitContainer.Panel1MinSize = 200
                _splitContainer.Panel2MinSize = 300
                ' Tree panel width — enough for longer node / installation /
                ' instance names without losing much content-panel real estate.
                Dim desiredWidth = 280
                Dim maxAllowed = _splitContainer.Width - _splitContainer.Panel2MinSize - _splitContainer.SplitterWidth
                If desiredWidth > maxAllowed Then desiredWidth = maxAllowed
                If desiredWidth < _splitContainer.Panel1MinSize Then desiredWidth = _splitContainer.Panel1MinSize
                _splitContainer.SplitterDistance = desiredWidth
            Catch
                ' Non-fatal — splitter stays at whatever default width
                ' it got; user can still drag to resize.
            End Try
        End Sub

        Private Sub InitializeComponent()
            Me.Text = "PowerGSM — Game Server Manager"
            Me.Size = New Size(1200, 800)
            Me.StartPosition = FormStartPosition.CenterScreen
            Me.MinimumSize = New Size(800, 600)

            ' ---- Menu ----
            _menuStrip = New MenuStrip()

            Dim fileMenu As New ToolStripMenuItem("&File")
            Dim exitItem As New ToolStripMenuItem("E&xit", Nothing, Sub(s, e) Me.Close())
            exitItem.ShortcutKeys = Keys.Alt Or Keys.F4
            fileMenu.DropDownItems.Add(exitItem)

            Dim nodesMenu As New ToolStripMenuItem("&Nodes")
            Dim addNodeItem As New ToolStripMenuItem("&Add Node...", Nothing,
                Sub(s, e) OnAddNode())
            Dim newInstallItem As New ToolStripMenuItem("New &Installation...", Nothing,
                Sub(s, e) OnNewInstallation())
            nodesMenu.DropDownItems.Add(addNodeItem)
            nodesMenu.DropDownItems.Add(newInstallItem)

            Dim toolsMenu As New ToolStripMenuItem("&Tools")
            Dim historyItem As New ToolStripMenuItem("&History...", Nothing,
                Sub(s, e) OnOpenHistory(Nothing))
            Dim reloadPluginsItem As New ToolStripMenuItem("&Reload Plugins", Nothing,
                Sub(s, e) OnReloadPlugins())
            Dim pluginStatusItem As New ToolStripMenuItem("&Plugin Status...", Nothing,
                Sub(s, e) OnPluginStatus())
            Dim steamCredsItem As New ToolStripMenuItem("&Steam Credentials...", Nothing,
                Sub(s, e) OnSteamCredentials())
            Dim automationItem As New ToolStripMenuItem("&Automation Rules...", Nothing,
                Sub(s, e) OnAutomationRules())
            Dim notificationsItem As New ToolStripMenuItem("&Notifications...", Nothing,
                Sub(s, e) OnNotificationsClicked(s, e))
            Dim settingsItem As New ToolStripMenuItem("S&ettings...", Nothing,
                Sub(s, e) OnSettings())
            toolsMenu.DropDownItems.Add(historyItem)
            toolsMenu.DropDownItems.Add(New ToolStripSeparator())
            toolsMenu.DropDownItems.Add(reloadPluginsItem)
            toolsMenu.DropDownItems.Add(pluginStatusItem)
            Dim openPluginsFolderItem As New ToolStripMenuItem("Open Plugins &Folder", Nothing,
                Sub(s, e) OnOpenPluginsFolder())
            toolsMenu.DropDownItems.Add(openPluginsFolderItem)
            toolsMenu.DropDownItems.Add(New ToolStripSeparator())
            toolsMenu.DropDownItems.Add(steamCredsItem)
            toolsMenu.DropDownItems.Add(New ToolStripSeparator())
            toolsMenu.DropDownItems.Add(automationItem)
            toolsMenu.DropDownItems.Add(notificationsItem)
            toolsMenu.DropDownItems.Add(settingsItem)

            _menuStrip.Items.AddRange(New ToolStripItem() {fileMenu, nodesMenu, toolsMenu})

            ' Help menu added in 5f-1 alongside the About dialog.
            ' Help is conventionally the rightmost top-level menu so
            ' it goes last. About... is the only item for now;
            ' future entries (online docs, check for updates, report
            ' a bug) can join it without restructuring.
            Dim helpMenu As New ToolStripMenuItem("&Help")
            Dim aboutItem As New ToolStripMenuItem("&About PowerGSM...", Nothing,
                Sub(s, e) OnAbout())
            helpMenu.DropDownItems.Add(aboutItem)
            _menuStrip.Items.Add(helpMenu)

            Me.MainMenuStrip = _menuStrip

            ' ---- Status bar ----
            ' Two labels: a left-aligned spring label that fills
            ' available width and carries the rolling status
            ' message, and a right-aligned fixed label showing the
            ' app version for passive visibility ("what version am
            ' I running?" without opening Help → About). Order
            ' matters: the spring label must be added FIRST so the
            ' version label sits to its right.
            _statusStrip = New StatusStrip()
            _statusLabel = New ToolStripStatusLabel("Ready")
            _statusLabel.Spring = True
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft
            _statusStrip.Items.Add(_statusLabel)

            _versionStatusLabel = New ToolStripStatusLabel(GetVersionStatusText())
            _versionStatusLabel.Spring = False
            _versionStatusLabel.TextAlign = ContentAlignment.MiddleRight
            _versionStatusLabel.ForeColor = SystemColors.GrayText
            ' Single-click on the version label opens the About
            ' dialog — a conventional shortcut for users who notice
            ' the version and want a fuller view (protocol/contracts
            ' axes, git SHA, blurb).
            AddHandler _versionStatusLabel.Click, Sub(s, e) OnAbout()
            _statusStrip.Items.Add(_versionStatusLabel)

            ' ---- Split container ----
            ' Setting MinSize/SplitterDistance values at construction
            ' time throws if the container's internal Width is smaller
            ' than Panel1MinSize + Panel2MinSize + SplitterWidth. Defer
            ' those to the Shown handler where the layout has settled.
            _splitContainer = New SplitContainer()
            _splitContainer.Dock = DockStyle.Fill
            _splitContainer.SplitterWidth = 4
            _splitContainer.FixedPanel = FixedPanel.Panel1

            ' ---- Tree view ----
            _treeView = New TreeView()
            _treeView.Dock = DockStyle.Fill
            _treeView.HideSelection = False
            _treeView.ShowLines = True
            _treeView.ShowPlusMinus = True
            _treeView.ShowRootLines = True
            _treeView.Font = New Font("Segoe UI", 9.5F)
            AddHandler _treeView.NodeMouseClick, AddressOf TreeView_NodeMouseClick
            _splitContainer.Panel1.Controls.Add(_treeView)

            ' ---- Content panel ----
            _contentPanel = New Panel()
            _contentPanel.Dock = DockStyle.Fill
            _contentPanel.Padding = New Padding(8)
            _splitContainer.Panel2.Controls.Add(_contentPanel)

            ' ---- Assembly ----
            Me.Controls.Add(_splitContainer)
            Me.Controls.Add(_statusStrip)
            Me.Controls.Add(_menuStrip)
        End Sub

        ' ============================================================
        '  Tree building
        ' ============================================================

        Private Sub BuildTree()
            _treeView.Nodes.Clear()

            _nodesRoot = New TreeNode("Nodes")
            _nodesRoot.Tag = "root:nodes"

            _automationRoot = New TreeNode("Automation Rules")
            _automationRoot.Tag = "root:automation"

            _settingsRoot = New TreeNode("Settings")
            _settingsRoot.Tag = "root:settings"

            _treeView.Nodes.AddRange(New TreeNode() {
                _nodesRoot, _automationRoot, _settingsRoot
            })

            ' Populate from database
            RefreshNodeTree()

            _nodesRoot.Expand()

            ' Restore the user's prior expansion state on top of
            ' RefreshNodeTree's default. RefreshNodeTree's own
            ' capture/restore logic preserves expansion across
            ' in-session refreshes, but the very first refresh on
            ' startup has no prior state to capture — the saved
            ' setting fills that gap so users don't re-expand the
            ' same nodes every launch.
            LoadAndApplySavedExpansion()
        End Sub

        ''' <summary>
        ''' Read the saved tree expansion tags from AppSettings
        ''' and expand any matching tree nodes. Wrapped in BeginUpdate
        ''' to suppress flicker, and Try/Catch to make a corrupt or
        ''' unparseable setting fall back gracefully to the default
        ''' expansion (Nodes root only).
        ''' </summary>
        Private Sub LoadAndApplySavedExpansion()
            Try
                Dim json As String = ""
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    json = db.GetSetting(GsmDataExtensions.SettingKeys.TreeExpandedTags, "")
                End Using
                If String.IsNullOrEmpty(json) Then Return

                Dim tags = JsonSerializer.Deserialize(Of List(Of String))(json)
                If tags Is Nothing OrElse tags.Count = 0 Then Return

                Dim tagSet As New HashSet(Of String)(tags, StringComparer.Ordinal)
                _treeView.BeginUpdate()
                Try
                    RestoreExpandedTags(_treeView.Nodes, tagSet)
                Finally
                    _treeView.EndUpdate()
                End Try
            Catch
                ' Bad JSON, missing column, or any other failure —
                ' degrade silently to the default expansion. The
                ' next save will overwrite the bad value with a
                ' fresh one.
            End Try
        End Sub

        ''' <summary>
        ''' Persist the current set of expanded tree-node tags to
        ''' AppSettings so the next launch can restore them. Called
        ''' from FormClosing rather than via a per-toggle save —
        ''' rapid expand/collapse during navigation shouldn't
        ''' produce a flurry of DB writes.
        ''' </summary>
        Private Sub MainForm_FormClosing(sender As Object, e As FormClosingEventArgs)
            Try
                Dim tags As New HashSet(Of String)(StringComparer.Ordinal)
                CollectExpandedTags(_treeView.Nodes, tags)

                ' Always write — even an empty list — so going from
                ' "some expanded" to "none expanded" persists rather
                ' than retaining the previous run's state.
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    db.SetSetting(GsmDataExtensions.SettingKeys.TreeExpandedTags,
                                   JsonSerializer.Serialize(tags.ToList()))
                    db.SaveChanges()
                End Using
            Catch
                ' Closing should never block on a persistence failure.
            End Try
        End Sub

        ''' <summary>
        ''' Reloads the tree from the database. Safe to call after
        ''' adding/removing nodes, installations, or instances.
        '''
        ''' Preserves UI state across the rebuild:
        '''   - Which nodes were expanded (keyed by Tag value, which
        '''     is stable across rebuilds even though TreeNode
        '''     references aren't)
        '''   - Which node was selected, so the user's context isn't
        '''     lost when something elsewhere triggers a refresh
        '''     (e.g. an InstallationPanel reorder or a delete-instance
        '''     cascade)
        '''
        ''' Without this preservation, every refresh collapsed the
        ''' tree to its initial "only Nodes root expanded" state,
        ''' which was disorienting after even routine actions.
        ''' </summary>
        Public Sub RefreshNodeTree()
            ' Capture state BEFORE clearing
            Dim expandedTags As New HashSet(Of String)(StringComparer.Ordinal)
            CollectExpandedTags(_treeView.Nodes, expandedTags)
            Dim selectedTag As String = Nothing
            If _treeView.SelectedNode IsNot Nothing AndAlso
               _treeView.SelectedNode.Tag IsNot Nothing Then
                selectedTag = _treeView.SelectedNode.Tag.ToString()
            End If

            ' Suppress TreeView repaints across the entire rebuild.
            ' Without BeginUpdate/EndUpdate the control repaints on
            ' every Nodes.Add(), Expand(), and SelectedNode change —
            ' so a tree with N total items takes ~N repaint cycles
            ' to refresh, producing a visible flicker and a perceived
            ' lag after Edit Instance / Edit Installation closes.
            ' Wrapping the whole rebuild collapses that to one repaint
            ' at the end. Try/Finally guarantees we re-enable drawing
            ' even if the DB scope or one of the helpers throws —
            ' otherwise the tree would be left frozen and unusable.
            _treeView.BeginUpdate()
            Try
                _nodesRoot.Nodes.Clear()

                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    Dim nodes = db.Nodes.ToList()
                    For Each nodeEntity In nodes
                        Dim nodeTreeNode As New TreeNode($"{nodeEntity.DisplayName} ({nodeEntity.HostAddress}:{nodeEntity.Port})")
                        nodeTreeNode.Tag = $"node:{nodeEntity.NodeId}"

                        ' Load installations for this node
                        Dim installations = db.Installations.
                            Where(Function(i) i.NodeId = nodeEntity.NodeId).
                            ToList()

                        For Each installEntity In installations
                            Dim installTreeNode As New TreeNode($"{installEntity.DisplayName} ({installEntity.GameId})")
                            installTreeNode.Tag = $"installation:{installEntity.InstallationId}"

                            ' Load instances for this installation. Order
                            ' by SortOrder so the tree mirrors the
                            ' InstallationPanel's instances list —
                            ' otherwise reordering on the panel wouldn't
                            ' visibly affect the tree even after we
                            ' refresh it here.
                            Dim instances = db.Instances.
                                Where(Function(i) i.InstallationId = installEntity.InstallationId).
                                OrderBy(Function(i) i.SortOrder).
                                ThenBy(Function(i) i.CreatedUtc).
                                ToList()

                            For Each instanceEntity In instances
                                Dim instanceTreeNode As New TreeNode(instanceEntity.DisplayName)
                                instanceTreeNode.Tag = $"instance:{instanceEntity.InstanceId}"
                                installTreeNode.Nodes.Add(instanceTreeNode)
                            Next

                            nodeTreeNode.Nodes.Add(installTreeNode)
                        Next

                        _nodesRoot.Nodes.Add(nodeTreeNode)
                    Next
                End Using

                ' Restore expansion state. The Nodes root is always
                ' expanded by initial BuildTree; preserve that even if
                ' the user collapsed it (it's hard to navigate when
                ' collapsed).
                RestoreExpandedTags(_treeView.Nodes, expandedTags)
                If Not _nodesRoot.IsExpanded Then _nodesRoot.Expand()

                ' Restore selection. The TreeNode reference changed but
                ' tags are stable, so we walk to find the matching one.
                ' Suppress the AfterSelect handler's panel-swap side effect
                ' during this restoration: re-selecting the same node
                ' shouldn't tear down and rebuild the currently-displayed
                ' panel (which would destroy any work-in-progress state
                ' on it, e.g. a listview selection a caller just set).
                If selectedTag IsNot Nothing Then
                    Dim toSelect = FindNodeByTag(_treeView.Nodes, selectedTag)
                    If toSelect IsNot Nothing Then
                        _suppressTreeAfterSelect = True
                        Try
                            _treeView.SelectedNode = toSelect
                        Finally
                            _suppressTreeAfterSelect = False
                        End Try
                    End If
                End If
            Finally
                _treeView.EndUpdate()
            End Try
        End Sub

        ''' <summary>
        ''' Walks the tree collecting Tag values of expanded nodes.
        ''' Recursive because expansion can be at any depth.
        ''' </summary>
        Private Sub CollectExpandedTags(nodes As TreeNodeCollection,
                                          tags As HashSet(Of String))
            For Each n As TreeNode In nodes
                If n.IsExpanded AndAlso n.Tag IsNot Nothing Then
                    tags.Add(n.Tag.ToString())
                End If
                If n.Nodes.Count > 0 Then CollectExpandedTags(n.Nodes, tags)
            Next
        End Sub

        ''' <summary>
        ''' Re-expands any node whose tag was in the captured set.
        ''' Must walk the new tree because TreeNode references
        ''' from before the rebuild are stale.
        ''' </summary>
        Private Sub RestoreExpandedTags(nodes As TreeNodeCollection,
                                          tags As HashSet(Of String))
            For Each n As TreeNode In nodes
                If n.Tag IsNot Nothing AndAlso tags.Contains(n.Tag.ToString()) Then
                    n.Expand()
                End If
                If n.Nodes.Count > 0 Then RestoreExpandedTags(n.Nodes, tags)
            Next
        End Sub

        ''' <summary>
        ''' Find the first node in the tree whose tag matches.
        ''' Used to restore selection after a rebuild.
        ''' </summary>
        Private Function FindNodeByTag(nodes As TreeNodeCollection,
                                         tag As String) As TreeNode
            For Each n As TreeNode In nodes
                If n.Tag IsNot Nothing AndAlso
                   String.Equals(n.Tag.ToString(), tag, StringComparison.Ordinal) Then
                    Return n
                End If
                If n.Nodes.Count > 0 Then
                    Dim found = FindNodeByTag(n.Nodes, tag)
                    If found IsNot Nothing Then Return found
                End If
            Next
            Return Nothing
        End Function

        ' ============================================================
        '  Tree selection
        ' ============================================================

        Private Sub TreeView_AfterSelect(sender As Object, e As TreeViewEventArgs) Handles _treeView.AfterSelect
            If _suppressTreeAfterSelect Then Return
            If e.Node Is Nothing OrElse e.Node.Tag Is Nothing Then Return

            Dim tag = e.Node.Tag.ToString()
            Dim parts = tag.Split(":"c, 2)
            If parts.Length < 2 Then Return

            Dim kind = parts(0)
            Dim entityId = parts(1)

            Select Case kind
                Case "root"
                    Select Case entityId
                        Case "nodes"
                            ShowPanel(New WelcomePanel())
                            ' "automation" and "settings" are intentional
                            ' no-ops on selection — their windows open only
                            ' on double-click (see TreeView_NodeMouseDoubleClick).
                            ' Two reasons:
                            '   1. Misclicks while navigating the tree would
                            '      otherwise pop the windows up unintentionally.
                            '   2. AfterSelect only fires when selection
                            '      CHANGES, so once the window was closed,
                            '      clicking the same node again wouldn't
                            '      reopen it — the user had to detour through
                            '      another node first. NodeMouseDoubleClick
                            '      fires on every double-click regardless
                            '      of selection state, so re-opening works.
                    End Select

                Case "node"
                    ShowPanel(New NodePanel(entityId))

                Case "installation"
                    ShowPanel(New InstallationPanel(entityId))

                Case "instance"
                    ShowPanel(New InstancePanel(entityId))

            End Select
        End Sub

        ''' <summary>
        ''' Double-click handler for tree nodes. Currently only the
        ''' Automation Rules and Settings root nodes do anything on
        ''' double-click — they open their respective windows. Other
        ''' kinds fall through (TreeView's default behaviour for
        ''' nodes with children is to toggle expansion on double-click,
        ''' which is preserved because we don't suppress it).
        ''' </summary>
        Private Sub TreeView_NodeMouseDoubleClick(sender As Object, e As TreeNodeMouseClickEventArgs) _
                Handles _treeView.NodeMouseDoubleClick
            If e.Node Is Nothing OrElse e.Node.Tag Is Nothing Then Return

            Dim tag = e.Node.Tag.ToString()
            Dim parts = tag.Split(":"c, 2)
            If parts.Length < 2 Then Return

            If parts(0) <> "root" Then Return

            Select Case parts(1)
                Case "automation"
                    OnAutomationRules()
                Case "settings"
                    OnSettings()
            End Select
        End Sub

        ' ============================================================
        '  Panel management
        ' ============================================================

        Private Sub ShowPanel(panel As UserControl)
            If _currentPanel IsNot Nothing Then
                _contentPanel.Controls.Remove(_currentPanel)
                _currentPanel.Dispose()
            End If
            _currentPanel = panel
            panel.Dock = DockStyle.Fill
            _contentPanel.Controls.Add(panel)
        End Sub

        Public Sub SetStatus(text As String)
            _statusLabel.Text = text
        End Sub

        ''' <summary>
        ''' Compose the right-aligned version label for the status
        ''' bar. Reads the build version off this assembly via
        ''' AssemblyInformationalVersion (set indirectly by
        ''' Directory.Build.props' Version property), strips any
        ''' "+sha" suffix the SDK appends in source-linked builds,
        ''' and prefixes "v" so the label reads e.g. "v0.1.0".
        ''' Falls back through AssemblyVersion to a literal
        ''' "v0.0.0" so the label is always present.
        ''' </summary>
        Private Function GetVersionStatusText() As String
            Try
                Dim asm = Assembly.GetExecutingAssembly()
                Dim infoAttr = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
                Dim raw As String = Nothing
                If infoAttr IsNot Nothing Then raw = infoAttr.InformationalVersion
                If String.IsNullOrEmpty(raw) Then
                    raw = asm.GetName().Version?.ToString(3)
                End If
                If String.IsNullOrEmpty(raw) Then Return "v0.0.0"
                Dim plus = raw.IndexOf("+"c)
                If plus >= 0 Then raw = raw.Substring(0, plus)
                Return "v" & raw
            Catch
                Return "v0.0.0"
            End Try
        End Function

        ''' <summary>
        ''' Open the modal Help → About dialog. Reachable from the
        ''' Help menu and from clicking the version label in the
        ''' status bar.
        ''' </summary>
        Private Sub OnAbout()
            Using dlg As New AboutForm()
                dlg.ShowDialog(Me)
            End Using
        End Sub

        ' ============================================================
        '  Menu handlers (stubs for Phase 3)
        ' ============================================================

        Private Sub OnAddNode()
            Using dlg As New NodeSetupForm()
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshNodeTree()
                End If
            End Using
        End Sub

        Private Sub OnNewInstallation(Optional preselectedNodeId As String = Nothing)
            ' If no preselect was passed but the user has a node
            ' currently selected in the tree, infer it. This handles
            ' the Tools / Nodes menu path: a user who's been clicking
            ' around on a specific node almost certainly wants that
            ' node populated when they pick "New Installation".
            ' Right-click "Add Installation..." already passes the
            ' explicit ID and short-circuits this inference.
            If String.IsNullOrEmpty(preselectedNodeId) Then
                Dim selectedNode = _treeView.SelectedNode
                If selectedNode IsNot Nothing AndAlso selectedNode.Tag IsNot Nothing Then
                    Dim parts = selectedNode.Tag.ToString().Split(":"c, 2)
                    If parts.Length = 2 AndAlso parts(0) = "node" Then
                        preselectedNodeId = parts(1)
                    End If
                End If
            End If

            Using dlg As New NewInstallationForm(preselectedNodeId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshNodeTree()
                End If
            End Using
        End Sub

        Private Sub OnReloadPlugins()
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return
            Dim orphanDetector = ManagerProgram.Services.GetService(Of PluginOrphanDetector)()
            Dim summary = registry.ReloadAll(orphanDetector)
            SetStatus($"Plugins reloaded: {summary.LoadedPlugins.Count} loaded, {summary.CompilationErrors.Count} errors")
        End Sub

        Private Sub OnPluginStatus()
            Using dlg As New PluginStatusForm()
                dlg.ShowDialog(Me)
            End Using
        End Sub

        Private Sub OnOpenPluginsFolder()
            Dim pluginsPath = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins")
            If Not IO.Directory.Exists(pluginsPath) Then
                IO.Directory.CreateDirectory(pluginsPath)
            End If
            Process.Start("explorer.exe", pluginsPath)
        End Sub

        Private Sub OnSteamCredentials()
            Using dlg As New SteamCredentialsForm()
                dlg.ShowDialog(Me)
            End Using
        End Sub

        ''' <summary>
        ''' Open the Automation Rules window. Public so other forms
        ''' (e.g. EditInstanceForm's drift redirect) can request it
        ''' through MainForm rather than instantiating their own —
        ''' keeps the "only one open at a time" invariant intact.
        ''' </summary>
        Public Sub OnAutomationRules()
            ' Non-modal: a status/inspection window users want
            ' to keep open while rules fire in the background.
            ' Re-clicking the menu or tree node brings the
            ' existing window forward instead of spawning a
            ' duplicate.
            If _automationWindow IsNot Nothing AndAlso Not _automationWindow.IsDisposed Then
                If _automationWindow.WindowState = FormWindowState.Minimized Then
                    _automationWindow.WindowState = FormWindowState.Normal
                End If
                _automationWindow.Activate()
                Return
            End If
            _automationWindow = New AutomationRulesForm()
            ' Drop the tracked reference when the form closes so
            ' the next click opens a fresh one.
            AddHandler _automationWindow.FormClosed,
                Sub(s, ev) _automationWindow = Nothing
            ' Show with MainForm as owner so the window stays in
            ' front of MainForm regardless of focus games. Earlier
            ' attempt with no-owner + BeginInvoke(Activate) didn't
            ' work for the tree-click path: the mouse-up event on
            ' the tree fires AFTER our deferred Activate runs, and
            ' the system's "focus follows the latest click" rule
            ' beats our Activate. Owner-coupling sidesteps the race
            ' entirely — owned windows are always above their owner
            ' in z-order, no matter who clicks what. Side effect:
            ' minimising MainForm minimises this window too. That's
            ' arguably the desired behaviour for a child window of
            ' the app.
            _automationWindow.Show(Me)
        End Sub

        Private Sub OnNotificationsClicked(sender As Object, e As EventArgs)
            Using f As New NotificationsForm()
                f.ShowDialog(Me)
            End Using
        End Sub

        Private Sub OnSettings()
            Using dlg As New SettingsForm()
                dlg.ShowDialog(Me)
            End Using
        End Sub

        ''' <summary>
        ''' Opens the non-modal History window. filter is Nothing
        ''' for the Tools-menu path (unfiltered); InstancePanel
        ''' launches with a pre-built filter to narrow to the
        ''' current instance's session and recent time.
        ''' </summary>
        Public Sub OnOpenHistory(filter As GSM.Manager.Core.HistoryFilter)
            Dim win As New HistoryWindow(filter)
            ' Show without owner so the window can survive MainForm
            ' being minimized / backgrounded. User can freely switch
            ' between this and other windows.
            win.Show()
        End Sub

        ' ============================================================
        '  Context menus (right-click on tree nodes)
        ' ============================================================

        Private Sub TreeView_NodeMouseClick(sender As Object, e As TreeNodeMouseClickEventArgs)
            If e.Button <> MouseButtons.Right Then Return

            _treeView.SelectedNode = e.Node
            If e.Node.Tag Is Nothing Then Return

            Dim tag = e.Node.Tag.ToString()
            Dim parts = tag.Split(":"c, 2)
            If parts.Length < 2 Then Return

            Dim kind = parts(0)
            Dim entityId = parts(1)
            Dim menu As New ContextMenuStrip()

            Select Case kind
                Case "node"
                    menu.Items.Add("Add Installation...", Nothing,
                        Sub(s, ev) OnNewInstallation(entityId))
                    menu.Items.Add("Edit Node...", Nothing,
                        Sub(s, ev) OnEditNode(entityId))
                    menu.Items.Add(New ToolStripSeparator())
                    menu.Items.Add("Delete Node", Nothing,
                        Sub(s, ev) OnDeleteNode(entityId))

                Case "installation"
                    ' "Add Instance..." honours the plugin's
                    ' MaxInstancesPerInstallation. When the limit is
                    ' reached, the item is greyed out with an
                    ' explanatory suffix in its label and a tooltip
                    ' explaining the why and the workaround. Users
                    ' who need another server are nudged toward
                    ' creating a separate installation. The form's
                    ' OnSave check is the defence-in-depth backstop
                    ' for any path that bypasses this menu.
                    Dim addInstanceItem As New ToolStripMenuItem(
                        "Add Instance...", Nothing,
                        Sub(s, ev) OnAddInstance(entityId))
                    Dim addLimit = CheckAddInstanceLimit(entityId)
                    If addLimit.Blocked Then
                        addInstanceItem.Enabled = False
                        addInstanceItem.Text = $"Add Instance...  ({addLimit.SuffixLabel})"
                        addInstanceItem.ToolTipText = addLimit.Tooltip
                    End If
                    menu.Items.Add(addInstanceItem)

                    menu.Items.Add("Edit Installation...", Nothing,
                        Sub(s, ev) OnEditInstallation(entityId))
                    menu.Items.Add("Update Installation", Nothing,
                        Sub(s, ev) OnUpdateInstallation(entityId))
                    menu.Items.Add(New ToolStripSeparator())
                    menu.Items.Add("Delete Installation", Nothing,
                        Sub(s, ev) OnDeleteInstallation(entityId))

                Case "instance"
                    menu.Items.Add("Start", Nothing,
                        Async Sub(s, ev) Await OnStartInstance(entityId))
                    menu.Items.Add("Stop", Nothing,
                        Async Sub(s, ev) Await OnStopInstance(entityId))
                    menu.Items.Add("Restart", Nothing,
                        Async Sub(s, ev) Await OnRestartInstance(entityId))
                    menu.Items.Add(New ToolStripSeparator())
                    menu.Items.Add("View Logs", Nothing,
                        Sub(s, ev)
                            Dim logForm As New LogViewerForm(entityId)
                            logForm.Show()
                        End Sub)
                    menu.Items.Add("Edit Instance", Nothing,
                        Sub(s, ev) OnEditInstance(entityId))
                    menu.Items.Add(New ToolStripSeparator())
                    menu.Items.Add("Delete Instance", Nothing,
                        Sub(s, ev) OnDeleteInstance(entityId))

                Case Else
                    Return
            End Select

            menu.Show(_treeView, e.Location)
        End Sub

        ' ============================================================
        '  Node CRUD
        ' ============================================================

        Private Sub OnEditNode(nodeId As String)
            Using dlg As New NodeSetupForm(nodeId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshNodeTree()
                End If
            End Using
        End Sub

        Private Sub OnDeleteNode(nodeId As String)
            Dim confirm = MessageBox.Show(
                "Delete this node and all its installations and instances?",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If confirm <> DialogResult.Yes Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Delete instances belonging to this node's installations
                Dim installIds = db.Installations.
                    Where(Function(i) i.NodeId = nodeId).
                    Select(Function(i) i.InstallationId).
                    ToList()

                Dim instances = db.Instances.
                    Where(Function(i) installIds.Contains(i.InstallationId)).
                    ToList()
                db.Instances.RemoveRange(instances)

                ' Delete installations
                Dim installations = db.Installations.
                    Where(Function(i) i.NodeId = nodeId).
                    ToList()
                db.Installations.RemoveRange(installations)

                ' Delete node
                Dim nodeEntity = db.Nodes.Find(nodeId)
                If nodeEntity IsNot Nothing Then
                    db.Nodes.Remove(nodeEntity)
                End If

                db.SaveChanges()
            End Using

            ' Remove cached client
            Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
            factory?.RemoveClient(nodeId)

            RefreshNodeTree()
            SetStatus("Node deleted")
        End Sub

        ' ============================================================
        '  Installation CRUD
        ' ============================================================

        Private Sub OnAddInstance(installationId As String)
            ' Defensive re-check: the menu item's Enabled state
            ' should already prevent reaching here when the limit
            ' is hit, but a TOCTOU window exists between menu
            ' render and click (another window adding an instance
            ' in the meantime). The form's OnSave does the
            ' authoritative check; this is just early UX so the
            ' user sees the same message they would have on a
            ' freshly-rendered menu instead of being allowed to
            ' fill out a form that's about to refuse to save.
            Dim addLimit = CheckAddInstanceLimit(installationId)
            If addLimit.Blocked Then
                MessageBox.Show(addLimit.Tooltip, "Limit Reached",
                              MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Using dlg As New AddInstanceForm(installationId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshNodeTree()
                End If
            End Using
        End Sub

        ''' <summary>
        ''' Resolve the plugin for an installation and check whether
        ''' adding another instance would exceed the plugin's
        ''' MaxInstancesPerInstallation. Returns Blocked=False (with
        ''' empty suffix/tooltip) when:
        '''   - the plugin imposes no limit (Nothing),
        '''   - the installation or plugin can't be resolved (treated
        '''     as "don't block on missing data" — the form's check
        '''     covers any genuine error case), or
        '''   - any DB/registry call throws (defensive).
        '''
        ''' Returns Blocked=True with a short suffix label for the
        ''' menu and a longer tooltip explaining the limit and the
        ''' workaround when the count is at or above the limit.
        ''' </summary>
        Private Function CheckAddInstanceLimit(installationId As String) As AddInstanceLimitInfo
            Dim none = New AddInstanceLimitInfo With {.Blocked = False}
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim installEntity = db.Installations.Find(installationId)
                    If installEntity Is Nothing Then Return none

                    Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                    If registry Is Nothing Then Return none

                    Dim plugin As IGamePlugin = registry.GetPlugin(installEntity.GameId)
                    If plugin Is Nothing Then Return none

                    Dim limit = plugin.MaxInstancesPerInstallation
                    If Not limit.HasValue Then Return none

                    Dim existing = db.Instances.
                        Count(Function(i) i.InstallationId = installationId)
                    If existing < limit.Value Then Return none

                    ' Singular/plural niceties on the count side; the
                    ' limit side is almost always 1 in practice but
                    ' we plural-aware it anyway.
                    Dim limitWord = If(limit.Value = 1, "instance", "instances")
                    Return New AddInstanceLimitInfo With {
                        .Blocked = True,
                        .SuffixLabel = $"limit reached: {existing}/{limit.Value}",
                        .Tooltip = $"{plugin.DisplayName} supports a maximum of {limit.Value} {limitWord} per installation. " &
                                   $"This installation already has {existing}. " &
                                   "Create a separate installation to run another server."
                    }
                End Using
            Catch
                Return none
            End Try
        End Function

        ''' <summary>
        ''' Result of CheckAddInstanceLimit — small DTO so the helper
        ''' can return three pieces of information without a tuple
        ''' (which would clash with the codebase's preference for
        ''' named-class results, e.g. LastRunStatus in RemainingForms).
        ''' </summary>
        Private Class AddInstanceLimitInfo
            Public Property Blocked As Boolean
            Public Property SuffixLabel As String
            Public Property Tooltip As String
        End Class

        Private Sub OnEditInstallation(installationId As String)
            Using dlg As New EditInstallationForm(installationId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshNodeTree()
                    SetStatus("Installation updated")
                End If
            End Using
        End Sub

        Private Async Sub OnUpdateInstallation(installationId As String)
            Dim confirm = MessageBox.Show(
                "Update this installation? All instances will be stopped during the update.",
                "Confirm Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If confirm <> DialogResult.Yes Then Return

            SetStatus("Updating installation...")
            Dim installMgr = ManagerProgram.Services.GetService(Of InstallationManager)()
            If installMgr IsNot Nothing Then
                Dim ok = Await installMgr.UpdateAsync(installationId,
                    promptHandler:=AddressOf HandleSteamPrompt)
                SetStatus(If(ok, "Update completed", "Update failed"))
            End If
        End Sub

        Private Function HandleSteamPrompt(promptType As GSM.Node.Api.PromptType,
                                            message As String) As Task(Of String)
            Dim result As String = Nothing
            If Me.InvokeRequired Then
                Me.Invoke(Sub()
                              result = ShowSteamGuardDialog(promptType, message)
                          End Sub)
            Else
                result = ShowSteamGuardDialog(promptType, message)
            End If
            Return Task.FromResult(result)
        End Function

        Private Function ShowSteamGuardDialog(promptType As GSM.Node.Api.PromptType,
                                               message As String) As String
            Dim title = If(promptType = GSM.Node.Api.PromptType.TwoFactorCode,
                "Steam Mobile Authenticator", "Steam Guard Code")
            Dim prompt = If(String.IsNullOrEmpty(message),
                "Enter the code from your email or authenticator app:",
                message)
            Return Microsoft.VisualBasic.Interaction.InputBox(prompt, title, "")
        End Function

        Private Async Sub OnDeleteInstallation(installationId As String)
            Dim confirm = MessageBox.Show(
                "Delete this installation and all its instances?",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If confirm <> DialogResult.Yes Then Return

            Dim deleteFiles = MessageBox.Show(
                "Also delete the game server files from the node?",
                "Delete Files?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) = DialogResult.Yes

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(installationId)

                ' Tell node to clean up files
                If installEntity IsNot Nothing AndAlso deleteFiles Then
                    Try
                        Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                        If nodeEntity IsNot Nothing Then
                            Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
                            Dim client = factory.GetClient(
                                nodeEntity.NodeId, nodeEntity.HostAddress,
                                nodeEntity.Port, nodeEntity.AuthToken)
                            Await client.UninstallAsync(New GSM.Node.Api.UninstallRequest With {
                                .InstallationId = installationId,
                                .InstallPath = installEntity.InstallPath,
                                .DeleteFiles = True
                            }, Threading.CancellationToken.None)
                        End If
                    Catch ex As Exception
                        MessageBox.Show($"Warning: could not delete files on node: {ex.Message}",
                                      "Node Cleanup", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    End Try
                End If

                ' Delete from local DB
                Dim instances = db.Instances.
                    Where(Function(i) i.InstallationId = installationId).
                    ToList()
                db.Instances.RemoveRange(instances)
                If installEntity IsNot Nothing Then
                    db.Installations.Remove(installEntity)
                End If
                db.SaveChanges()
            End Using

            RefreshNodeTree()
            SetStatus("Installation deleted")
        End Sub

        ' ============================================================
        '  Instance operations
        ' ============================================================

        Private Async Function OnStartInstance(instanceId As String) As Task
            SetStatus($"Starting {instanceId}...")
            Dim instMgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If instMgr IsNot Nothing Then
                Dim ok = Await instMgr.StartInstanceAsync(instanceId)
                SetStatus(If(ok, $"Instance {instanceId} started", $"Failed to start {instanceId}"))
            End If
        End Function

        Private Async Function OnStopInstance(instanceId As String) As Task
            SetStatus($"Stopping {instanceId}...")
            Dim instMgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If instMgr IsNot Nothing Then
                Dim ok = Await instMgr.StopInstanceAsync(instanceId)
                SetStatus(If(ok, $"Instance {instanceId} stopped", $"Failed to stop {instanceId}"))
            End If
        End Function

        Private Async Function OnRestartInstance(instanceId As String) As Task
            SetStatus($"Restarting {instanceId}...")
            Dim instMgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If instMgr IsNot Nothing Then
                Dim ok = Await instMgr.RestartInstanceAsync(instanceId)
                SetStatus(If(ok, $"Instance {instanceId} restarted", $"Failed to restart {instanceId}"))
            End If
        End Function

        Private Sub OnEditInstance(instanceId As String)
            Using dlg As New EditInstanceForm(instanceId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshNodeTree()
                    SetStatus("Instance updated")
                End If
            End Using
        End Sub

        Private Sub OnDeleteInstance(instanceId As String)
            ' Two-stage confirmation: if the instance has an
            ' associated restart rule (the typical case for any
            ' instance the user has scheduled), we make it explicit
            ' that the rule goes too. Saves a "why is there an
            ' orphan rule in Automation Rules" surprise later.
            Dim hasRule As Boolean = False
            Dim ruleId As String = Nothing
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                If instanceEntity IsNot Nothing AndAlso
                   Not String.IsNullOrEmpty(instanceEntity.RestartRuleId) Then
                    ' Confirm the rule actually exists — RestartRuleId
                    ' could be stale (orphan record from a prior
                    ' inconsistent state). No warning needed for
                    ' a stale ID since deleting the instance won't
                    ' affect a non-existent rule.
                    Dim rule = db.AutomationRules.Find(instanceEntity.RestartRuleId)
                    If rule IsNot Nothing Then
                        hasRule = True
                        ruleId = rule.RuleId
                    End If
                End If
            End Using

            Dim message = If(hasRule,
                "Delete this instance? Its scheduled restart rule will also be removed.",
                "Delete this instance?")
            Dim confirm = MessageBox.Show(
                message,
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If confirm <> DialogResult.Yes Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                If instanceEntity IsNot Nothing Then
                    ' Cascade-delete the rule first so we don't leave
                    ' an orphan if the instance delete somehow fails.
                    ' Both deletes are in the same transaction —
                    ' SaveChanges commits both or neither.
                    If Not String.IsNullOrEmpty(ruleId) Then
                        Dim rule = db.AutomationRules.Find(ruleId)
                        If rule IsNot Nothing Then
                            db.AutomationRules.Remove(rule)
                        End If
                    End If
                    db.Instances.Remove(instanceEntity)
                    db.SaveChanges()
                End If
            End Using

            ' If we removed a rule, reload the engine so its in-memory
            ' rule set + cron timers stay consistent with the DB.
            If hasRule Then
                Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
                engine?.ReloadRules()
            End If

            RefreshNodeTree()
            SetStatus("Instance deleted")
        End Sub

    End Class

End Namespace