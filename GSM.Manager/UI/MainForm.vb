Imports System
Imports System.Diagnostics
Imports System.Drawing
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data

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

        ' ---- Tree root nodes ----
        Private _nodesRoot As TreeNode
        Private _automationRoot As TreeNode
        Private _settingsRoot As TreeNode

        ' ---- Current panel ----
        Private _currentPanel As UserControl

        Public Sub New()
            InitializeComponent()
            BuildTree()
            ShowPanel(New WelcomePanel())
        End Sub

        Private Sub InitializeComponent()
            Me.Text = "PowerGSM — Game Server Manager"
            Me.Size = New Size(1200, 800)
            Me.StartPosition = FormStartPosition.CenterScreen
            Me.MinimumSize = New Size(800, 600)

            ' ---- Menu ----
            _menuStrip = New MenuStrip()

            Dim fileMenu As New ToolStripMenuItem("&File")
            Dim exitItem As New ToolStripMenuItem("E&xit", Nothing,
                Sub(s, e) Me.Close())
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
            Dim reloadPluginsItem As New ToolStripMenuItem("&Reload Plugins", Nothing,
                Sub(s, e) OnReloadPlugins())
            Dim pluginStatusItem As New ToolStripMenuItem("&Plugin Status...", Nothing,
                Sub(s, e) OnPluginStatus())
            Dim steamCredsItem As New ToolStripMenuItem("&Steam Credentials...", Nothing,
                Sub(s, e) OnSteamCredentials())
            Dim realmCredsItem As New ToolStripMenuItem("&Realm Credentials...", Nothing,
                Sub(s, e) OnRealmCredentials())
            Dim automationItem As New ToolStripMenuItem("&Automation Rules...", Nothing,
                Sub(s, e) OnAutomationRules())
            Dim settingsItem As New ToolStripMenuItem("S&ettings...", Nothing,
                Sub(s, e) OnSettings())
            toolsMenu.DropDownItems.Add(reloadPluginsItem)
            toolsMenu.DropDownItems.Add(pluginStatusItem)
            Dim openPluginsFolderItem As New ToolStripMenuItem("Open Plugins &Folder", Nothing,
                Sub(s, e) OnOpenPluginsFolder())
            toolsMenu.DropDownItems.Add(openPluginsFolderItem)
            toolsMenu.DropDownItems.Add(New ToolStripSeparator())
            toolsMenu.DropDownItems.Add(steamCredsItem)
            toolsMenu.DropDownItems.Add(realmCredsItem)
            toolsMenu.DropDownItems.Add(New ToolStripSeparator())
            toolsMenu.DropDownItems.Add(automationItem)
            toolsMenu.DropDownItems.Add(settingsItem)

            _menuStrip.Items.AddRange(New ToolStripItem() {fileMenu, nodesMenu, toolsMenu})
            Me.MainMenuStrip = _menuStrip

            ' ---- Status bar ----
            _statusStrip = New StatusStrip()
            _statusLabel = New ToolStripStatusLabel("Ready")
            _statusLabel.Spring = True
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft
            _statusStrip.Items.Add(_statusLabel)

            ' ---- Split container ----
            _splitContainer = New SplitContainer()
            _splitContainer.Dock = DockStyle.Fill
            _splitContainer.SplitterDistance = 280
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
        End Sub

        ''' <summary>
        ''' Reloads the tree from the database. Safe to call after
        ''' adding/removing nodes, installations, or instances.
        ''' </summary>
        Public Sub RefreshNodeTree()
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

                        ' Load instances for this installation
                        Dim instances = db.Instances.
                            Where(Function(i) i.InstallationId = installEntity.InstallationId).
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
        End Sub

        ' ============================================================
        '  Tree selection
        ' ============================================================

        Private Sub TreeView_AfterSelect(sender As Object, e As TreeViewEventArgs) Handles _treeView.AfterSelect
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
                        Case "automation"
                            OnAutomationRules()
                        Case "settings"
                            OnSettings()
                    End Select

                Case "node"
                    ShowPanel(New NodePanel(entityId))

                Case "installation"
                    SetStatus($"Installation selected: {entityId}")
                    ' InstallationPanel comes in Phase 5

                Case "instance"
                    ShowPanel(New InstancePanel(entityId))

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

        Private Sub OnNewInstallation()
            Using dlg As New NewInstallationForm()
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

        Private Sub OnRealmCredentials()
            Using dlg As New RealmCredentialsForm()
                dlg.ShowDialog(Me)
            End Using
        End Sub

        Private Sub OnAutomationRules()
            Using dlg As New AutomationRulesForm()
                dlg.ShowDialog(Me)
            End Using
        End Sub

        Private Sub OnSettings()
            Using dlg As New SettingsForm()
                dlg.ShowDialog(Me)
            End Using
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
                        Sub(s, ev) OnNewInstallation())
                    menu.Items.Add("Edit Node...", Nothing,
                        Sub(s, ev) OnEditNode(entityId))
                    menu.Items.Add(New ToolStripSeparator())
                    menu.Items.Add("Delete Node", Nothing,
                        Sub(s, ev) OnDeleteNode(entityId))

                Case "installation"
                    menu.Items.Add("Add Instance...", Nothing,
                        Sub(s, ev) OnAddInstance(entityId))
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
            Using dlg As New AddInstanceForm(installationId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshNodeTree()
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

        Private Sub OnDeleteInstance(instanceId As String)
            Dim confirm = MessageBox.Show(
                "Delete this instance?",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If confirm <> DialogResult.Yes Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                If instanceEntity IsNot Nothing Then
                    db.Instances.Remove(instanceEntity)
                    db.SaveChanges()
                End If
            End Using

            RefreshNodeTree()
            SetStatus("Instance deleted")
        End Sub

    End Class

End Namespace
