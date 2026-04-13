Imports System.Drawing
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports GSM.Core
Imports GSM.Data
Imports GSM.Plugin

' ============================================================
'  MainForm
'
'  The application shell. Layout:
'
'  ┌─────────────────────────────────────────────────────────┐
'  │  Menu bar                                               │
'  ├──────────────┬──────────────────────────────────────────┤
'  │              │                                          │
'  │  Tree        │  Detail panel (swapped by selection)     │
'  │  (nodes /    │                                          │
'  │   installs / │                                          │
'  │   instances) │                                          │
'  │              │                                          │
'  ├──────────────┴──────────────────────────────────────────┤
'  │  Status bar                                             │
'  └─────────────────────────────────────────────────────────┘
'
'  The detail panel on the right swaps between:
'    - WelcomePanel      (nothing selected)
'    - InstancePanel     (instance selected)
'    - InstallationPanel (installation selected)
'    - NodePanel         (node selected)
'
'  State updates arrive via two paths:
'    1. InstanceManager.InstanceStateChanged event (push)
'    2. Background metrics poll every 30s (pull)
'  Both marshal to the UI thread via BeginInvoke.
'
'  WinForms threading rule: never touch a control from a
'  background thread. Always use BeginInvoke or Invoke.
' ============================================================

Public Class MainForm
    Inherits Form

    ' ---- Services (set by the DI bootstrapper in Program.vb) ----
    Public Property InstanceManager As InstanceManager
    Public Property InstallationManager As InstallationManager
    Public Property AutomationEngine As AutomationEngine
    Public Property PluginRegistry As PluginRegistry
    Public Property CredentialService As CredentialService
    Public Property DbFactory As IDbContextFactory(Of GsmDbContext)

    ' ---- Layout controls ----
    Private WithEvents _menuStrip As MenuStrip
    Private WithEvents _splitContainer As SplitContainer
    Private WithEvents _treeView As TreeView
    Private WithEvents _statusStrip As StatusStrip
    Private _statusLabel As ToolStripStatusLabel
    Private _statusNodeCount As ToolStripStatusLabel
    Private _statusRunningCount As ToolStripStatusLabel

    ' ---- Detail panels (only one visible at a time) ----
    Private _welcomePanel As WelcomePanel
    Private _instancePanel As InstancePanel
    Private _nodePanel As NodePanel
    Private _currentDetailControl As Control

    ' ---- Tree node tags ----
    ' Each TreeNode.Tag is one of these to identify what was selected.
    Private Class NodeTag
        Public Property NodeId As String
        Public Property DisplayName As String
    End Class
    Private Class InstallationTag
        Public Property InstallationId As String
        Public Property NodeId As String
        Public Property DisplayName As String
        Public Property GameId As String
    End Class
    Private Class InstanceTag
        Public Property InstanceId As String
        Public Property InstallationId As String
        Public Property NodeId As String
        Public Property DisplayName As String
        Public Property GameId As String
    End Class

    ' ---- Background tasks ----
    Private _cts As New CancellationTokenSource()
    Private _refreshTimer As System.Windows.Forms.Timer

    ' ---- Image list for tree icons ----
    Private _imageList As ImageList


    Public Sub New()
        InitializeComponent()
        Me.Text = "Game Server Manager"
        Me.Size = New Size(1200, 750)
        Me.MinimumSize = New Size(900, 600)
        Me.StartPosition = FormStartPosition.CenterScreen
        Me.Icon = SystemIcons.Application
    End Sub

    Protected Overrides Sub OnLoad(e As EventArgs)
        MyBase.OnLoad(e)
        BuildLayout()
        WireEvents()
        Task.Run(AddressOf LoadTreeAsync)
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        MyBase.OnFormClosing(e)
        _cts.Cancel()
        If _refreshTimer IsNot Nothing Then _refreshTimer.Stop()
    End Sub


    ' ============================================================
    '  LAYOUT CONSTRUCTION
    ' ============================================================

    Private Sub BuildLayout()
        ' ---- Image list for tree icons ----
        _imageList = New ImageList With {.ImageSize = New Size(16, 16)}
        _imageList.Images.Add("node",         SystemIcons.Application.ToBitmap())
        _imageList.Images.Add("installation", SystemIcons.Shield.ToBitmap())
        _imageList.Images.Add("running",      SystemIcons.Information.ToBitmap())
        _imageList.Images.Add("stopped",      SystemIcons.Warning.ToBitmap())
        _imageList.Images.Add("crashed",      SystemIcons.Error.ToBitmap())

        ' ---- Menu bar ----
        _menuStrip = New MenuStrip()
        BuildMenus()
        Controls.Add(_menuStrip)
        MainMenuStrip = _menuStrip

        ' ---- Status bar ----
        _statusStrip = New StatusStrip()
        _statusLabel = New ToolStripStatusLabel("Ready")
        _statusNodeCount = New ToolStripStatusLabel("Nodes: 0") With {
            .BorderSides = ToolStripStatusLabelBorderSides.Left
        }
        _statusRunningCount = New ToolStripStatusLabel("Running: 0") With {
            .BorderSides = ToolStripStatusLabelBorderSides.Left
        }
        _statusStrip.Items.AddRange({_statusLabel,
                                      New ToolStripStatusLabel() With {.Spring = True},
                                      _statusNodeCount,
                                      _statusRunningCount})
        Controls.Add(_statusStrip)

        ' ---- Split container ----
        _splitContainer = New SplitContainer With {
            .Dock = DockStyle.Fill,
            .SplitterDistance = 270,
            .Panel1MinSize = 200,
            .Panel2MinSize = 400
        }

        ' ---- Tree view (left panel) ----
        _treeView = New TreeView With {
            .Dock = DockStyle.Fill,
            .ImageList = _imageList,
            .ShowLines = True,
            .ShowPlusMinus = True,
            .FullRowSelect = True,
            .HideSelection = False
        }
        _splitContainer.Panel1.Controls.Add(_treeView)

        ' ---- Detail panels (right panel) ----
        _welcomePanel = New WelcomePanel With {.Dock = DockStyle.Fill}
        _instancePanel = New InstancePanel With {.Dock = DockStyle.Fill}
        _nodePanel = New NodePanel With {.Dock = DockStyle.Fill}

        ShowDetailPanel(_welcomePanel)
        _splitContainer.Panel2.Controls.Add(_welcomePanel)
        _splitContainer.Panel2.Controls.Add(_instancePanel)
        _splitContainer.Panel2.Controls.Add(_nodePanel)

        Controls.Add(_splitContainer)

        ' ---- Refresh timer ----
        _refreshTimer = New System.Windows.Forms.Timer With {.Interval = 30000}
        AddHandler _refreshTimer.Tick, AddressOf OnRefreshTick
        _refreshTimer.Start()
    End Sub

    Private Sub BuildMenus()
        ' File menu
        Dim fileMenu = New ToolStripMenuItem("&File")
        Dim addNodeItem = New ToolStripMenuItem("Add &Node...",
            Nothing, AddressOf OnAddNodeClick)
        Dim settingsItem = New ToolStripMenuItem("&Settings...",
            Nothing, AddressOf OnSettingsClick)
        Dim exitItem = New ToolStripMenuItem("E&xit",
            Nothing, Sub(s, e) Application.Exit())
        fileMenu.DropDownItems.AddRange({addNodeItem, settingsItem,
                                         New ToolStripSeparator(), exitItem})

        ' Plugins menu
        Dim pluginsMenu = New ToolStripMenuItem("&Plugins")
        Dim reloadItem = New ToolStripMenuItem("&Reload Plugins",
            Nothing, AddressOf OnReloadPluginsClick)
        Dim pluginStatusItem = New ToolStripMenuItem("Plugin &Status...",
            Nothing, AddressOf OnPluginStatusClick)
        pluginsMenu.DropDownItems.AddRange({reloadItem, pluginStatusItem})

        ' Credentials menu
        Dim credsMenu = New ToolStripMenuItem("&Credentials")
        Dim steamCredsItem = New ToolStripMenuItem("&Steam Accounts...",
            Nothing, AddressOf OnSteamCredentialsClick)
        Dim realmCredsItem = New ToolStripMenuItem("&Realm Credentials...",
            Nothing, AddressOf OnRealmCredentialsClick)
        credsMenu.DropDownItems.AddRange({steamCredsItem, realmCredsItem})

        _menuStrip.Items.AddRange({fileMenu, pluginsMenu, credsMenu})
    End Sub

    Private Sub ShowDetailPanel(panel As Control)
        ' Hide all detail panels then show the requested one.
        _welcomePanel.Visible = False
        _instancePanel.Visible = False
        _nodePanel.Visible = False
        panel.Visible = True
        panel.BringToFront()
        _currentDetailControl = panel
    End Sub


    ' ============================================================
    '  TREE LOADING
    ' ============================================================

    Friend Async Function LoadTreeAsync() As Task
        Try
            SetStatus("Loading...")
            Dim installations = Await InstallationManager.GetAllInstallationsAsync(_cts.Token)

            ' Marshal back to UI thread before touching any controls.
            BeginInvoke(Sub() PopulateTree(installations))
        Catch ex As Exception
            Dim errMsg = "Load error: " & ex.Message
            BeginInvoke(Sub() SetStatus(errMsg))
        End Try
    End Function

    Private Sub PopulateTree(installations As List(Of InstallationEntity))
        _treeView.BeginUpdate()
        _treeView.Nodes.Clear()

        ' Group installations by node.
        Dim byNode = installations.GroupBy(Function(i) i.NodeId)

        Dim nodeCount = 0
        For Each nodeGroup In byNode
            Dim nodeEntity = nodeGroup.First().Node
            Dim nodeName = If(nodeEntity?.DisplayName, nodeGroup.Key)

            Dim nodeTreeNode As New TreeNode(nodeName) With {
                .ImageKey = "node",
                .SelectedImageKey = "node",
                .Tag = New NodeTag With {
                    .NodeId = nodeGroup.Key,
                    .DisplayName = nodeName
                }
            }

            For Each installation In nodeGroup
                Dim installTreeNode As New TreeNode(installation.DisplayName) With {
                    .ImageKey = "installation",
                    .SelectedImageKey = "installation",
                    .Tag = New InstallationTag With {
                        .InstallationId = installation.InstallationId,
                        .NodeId = installation.NodeId,
                        .DisplayName = installation.DisplayName,
                        .GameId = installation.GameId
                    }
                }

                For Each instance In installation.Instances.OrderBy(Function(i) i.SortOrder)
                    Dim icon = StateToIcon(instance.LastKnownState)
                    Dim instanceTreeNode As New TreeNode(
                            $"{instance.DisplayName}  [{instance.LastKnownState}]") With {
                        .ImageKey = icon,
                        .SelectedImageKey = icon,
                        .Tag = New InstanceTag With {
                            .InstanceId = instance.InstanceId,
                            .InstallationId = instance.InstallationId,
                            .NodeId = installation.NodeId,
                            .DisplayName = instance.DisplayName,
                            .GameId = instance.GameId
                        }
                    }
                    installTreeNode.Nodes.Add(instanceTreeNode)
                Next

                nodeTreeNode.Nodes.Add(installTreeNode)
                installTreeNode.Expand()
            Next

            _treeView.Nodes.Add(nodeTreeNode)
            nodeTreeNode.Expand()
            nodeCount += 1
        Next

        _treeView.EndUpdate()

        Dim runningCount = installations.
            SelectMany(Function(i) i.Instances).
            Count(Function(i) i.LastKnownState = "Running")

        _statusNodeCount.Text = $"Nodes: {nodeCount}"
        _statusRunningCount.Text = $"Running: {runningCount}"
        SetStatus("Ready")
    End Sub

    Private Shared Function StateToIcon(state As String) As String
        Select Case state
            Case "Running", "Starting"  : Return "running"
            Case "Crashed", "CrashLoopHalted", "StartFailed" : Return "crashed"
            Case Else : Return "stopped"
        End Select
    End Function

    ' Update just the label on an existing instance tree node.
    ' Called when a state change event arrives - no full reload needed.
    Private Sub UpdateInstanceTreeNode(instanceId As String,
                                        newState As InstanceState)
        For Each nodeNode As TreeNode In _treeView.Nodes
            For Each installNode As TreeNode In nodeNode.Nodes
                For Each instanceNode As TreeNode In installNode.Nodes
                    Dim tag = TryCast(instanceNode.Tag, InstanceTag)
                    If tag IsNot Nothing AndAlso tag.InstanceId = instanceId Then
                        instanceNode.Text = $"{tag.DisplayName}  [{newState}]"
                        instanceNode.ImageKey = StateToIcon(newState.ToString())
                        instanceNode.SelectedImageKey = instanceNode.ImageKey

                        ' Update the running count in the status bar.
                        Dim running = 0
                        For Each n As TreeNode In _treeView.Nodes
                            For Each ii As TreeNode In n.Nodes
                                For Each inst As TreeNode In ii.Nodes
                                    If inst.ImageKey = "running" Then running += 1
                                Next
                            Next
                        Next
                        _statusRunningCount.Text = $"Running: {running}"
                        Return
                    End If
                Next
            Next
        Next
    End Sub


    ' ============================================================
    '  TREE SELECTION
    ' ============================================================

    Private Sub OnTreeAfterSelect(sender As Object,
                                   e As TreeViewEventArgs) Handles _treeView.AfterSelect

        If e.Node Is Nothing Then Return

        If TypeOf e.Node.Tag Is InstanceTag Then
            Dim tag = CType(e.Node.Tag, InstanceTag)
            _instancePanel.Bind(tag.InstanceId, tag.DisplayName, tag.GameId,
                                 InstanceManager, PluginRegistry)
            ShowDetailPanel(_instancePanel)

        ElseIf TypeOf e.Node.Tag Is NodeTag Then
            Dim tag = CType(e.Node.Tag, NodeTag)
            _nodePanel.Bind(tag.NodeId, tag.DisplayName)
            ShowDetailPanel(_nodePanel)

        Else
            ShowDetailPanel(_welcomePanel)
        End If
    End Sub

    Private Sub OnTreeNodeMouseDoubleClick(sender As Object,
                                            e As TreeNodeMouseClickEventArgs) _
            Handles _treeView.NodeMouseDoubleClick

        ' Double-click an instance to open its log viewer.
        If TypeOf e.Node.Tag Is InstanceTag Then
            Dim tag = CType(e.Node.Tag, InstanceTag)
            OpenLogViewer(tag.InstanceId, tag.DisplayName)
        End If
    End Sub


    ' ============================================================
    '  EVENT WIRING
    ' ============================================================

    Private Sub WireEvents()
        ' InstanceManager fires this on any state change.
        ' It arrives on a background thread - marshal to UI.
        AddHandler InstanceManager.InstanceStateChanged,
            Sub(instanceId, newState, reason)
                If InvokeRequired Then
                    BeginInvoke(Sub() OnInstanceStateChanged(instanceId, newState))
                Else
                    OnInstanceStateChanged(instanceId, newState)
                End If
            End Sub
    End Sub

    Private Sub OnInstanceStateChanged(instanceId As String,
                                        newState As InstanceState)
        UpdateInstanceTreeNode(instanceId, newState)

        ' If this instance is currently selected, refresh the detail panel.
        If _currentDetailControl Is _instancePanel AndAlso
           _instancePanel.CurrentInstanceId = instanceId Then
            _instancePanel.RefreshState(newState)
        End If
    End Sub

    Private Sub OnRefreshTick(sender As Object, e As EventArgs)
        Task.Run(AddressOf LoadTreeAsync)
    End Sub


    ' ============================================================
    '  LOG VIEWER
    ' ============================================================

    Friend Sub OpenLogViewer(instanceId As String, displayName As String)
        Dim existing = Application.OpenForms.OfType(Of LogViewerForm)().
            FirstOrDefault(Function(f) f.InstanceId = instanceId)

        If existing IsNot Nothing Then
            existing.BringToFront()
            Return
        End If

        Dim viewer As New LogViewerForm(instanceId, displayName, InstanceManager)
        viewer.Show(Me)
    End Sub


    ' ============================================================
    '  MENU HANDLERS
    ' ============================================================

    Private Sub OnAddNodeClick(sender As Object, e As EventArgs)
        Using dlg As New NodeSetupForm(CredentialService, DbFactory)
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                Task.Run(AddressOf LoadTreeAsync)
            End If
        End Using
    End Sub

    Private Sub OnSettingsClick(sender As Object, e As EventArgs)
        Using dlg As New SettingsForm(DbFactory)
            dlg.ShowDialog(Me)
        End Using
    End Sub

    Private Async Sub OnReloadPluginsClick(sender As Object, e As EventArgs)
        Task.Run(Async Function()
                     SetStatusThreadSafe("Reloading plugins...")
                     Try
                         Dim summary = Await PluginRegistry.ReloadAsync(_cts.Token)
                         BeginInvoke(Sub() ShowReloadSummary(summary))
                     Catch ex As Exception
                         Dim errMsg = "Reload error: " & ex.Message
                         SetStatusThreadSafe(errMsg)
                     End Try
                 End Function)
    End Sub

    Private Sub ShowReloadSummary(summary As PluginReloadSummary)
        SetStatus(summary.Message)

        ' If there were compile errors or orphans, show a dialog.
        If summary.Outcome = ReloadOutcome.CompileFailed OrElse
           summary.HasOrphans OrElse
           summary.DiscoveryErrors.Any() Then

            Using dlg As New PluginStatusForm(summary)
                dlg.ShowDialog(Me)
            End Using
        End If
    End Sub

    Private Sub OnPluginStatusClick(sender As Object, e As EventArgs)
        Dim statuses = PluginRegistry.GetLoadStatus()
        Using dlg As New PluginStatusForm(statuses)
            dlg.ShowDialog(Me)
        End Using
    End Sub

    Private Sub OnSteamCredentialsClick(sender As Object, e As EventArgs)
        Using dlg As New SteamCredentialsForm(CredentialService, PluginRegistry)
            dlg.ShowDialog(Me)
        End Using
    End Sub

    Private Sub OnRealmCredentialsClick(sender As Object, e As EventArgs)
        Using dlg As New RealmCredentialsForm(CredentialService, PluginRegistry)
            dlg.ShowDialog(Me)
        End Using
    End Sub


    ' ============================================================
    '  STATUS HELPERS
    ' ============================================================

    Private Sub SetStatus(message As String)
        _statusLabel.Text = message
    End Sub

    ' Thread-safe version for background tasks.
    Private Sub SetStatusThreadSafe(message As String)
        If InvokeRequired Then
            BeginInvoke(Sub() SetStatus(message))
        Else
            SetStatus(message)
        End If
    End Sub

    Private Sub InitializeComponent()
        ' Designer-generated stub - in practice VS generates this.
        ' Left minimal here since the layout is built in BuildLayout().
    End Sub

End Class
