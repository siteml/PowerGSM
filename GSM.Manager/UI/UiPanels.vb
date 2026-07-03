Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  UI Panels — content panels shown in the right side of MainForm
' ============================================================

Namespace GSM.Manager.UI

    ' ============================================================
    '  PanelIdLabel — shared helper for the dim, copyable "ID …"
    '  sub-label shown on the Node / Installation / Instance panels.
    '  The raw id is stashed in the label's Tag; a right-click
    '  "Copy ID" item copies it. AddressOf handler (not a per-call
    '  lambda) so there are no closure-lifetime surprises.
    ' ============================================================
    Friend Module PanelIdLabel

        Public Function Create(location As Point) As Label
            Dim lbl As New Label() With {
                .AutoSize = True,
                .Location = location,
                .ForeColor = Color.Gray,
                .Font = New Font("Segoe UI", 8.0F)
            }
            Dim menu As New ContextMenuStrip()
            Dim copyItem As New ToolStripMenuItem("Copy ID")
            AddHandler copyItem.Click, AddressOf OnCopyClick
            menu.Items.Add(copyItem)
            menu.Tag = lbl
            lbl.ContextMenuStrip = menu
            Return lbl
        End Function

        Public Sub SetId(lbl As Label, id As String)
            If lbl Is Nothing Then Return
            lbl.Tag = id
            lbl.Text = If(String.IsNullOrEmpty(id), "", "ID  " & id)
        End Sub

        Private Sub OnCopyClick(sender As Object, e As EventArgs)
            Dim item = TryCast(sender, ToolStripMenuItem)
            If item Is Nothing Then Return
            Dim menu = TryCast(item.Owner, ContextMenuStrip)
            If menu Is Nothing Then Return
            Dim lbl = TryCast(menu.Tag, Label)
            If lbl Is Nothing Then Return
            Dim id = TryCast(lbl.Tag, String)
            If String.IsNullOrEmpty(id) Then Return
            Try
                Clipboard.SetText(id)
            Catch
            End Try
        End Sub

    End Module

    ' ============================================================
    '  WelcomePanel — shown when no specific node/instance selected
    ' ============================================================

    Public Class WelcomePanel
        Inherits UserControl

        ' PictureBox owns the logo bitmap. WelcomePanel instances are
        ' swapped out as the user navigates the tree, so we need to
        ' dispose the bitmap in Dispose(disposing) rather than relying
        ' on the GC — otherwise every trip back to the Nodes root
        ' leaks another copy.
        Private _logoBox As PictureBox

        Public Sub New()
            _logoBox = New PictureBox()
            _logoBox.Location = New Point(20, 20)
            _logoBox.Size = New Size(128, 128)
            _logoBox.SizeMode = PictureBoxSizeMode.Zoom
            _logoBox.Image = FormIconHelper.GetLargeBitmap()

            Dim titleLabel As New Label()
            titleLabel.Text = "PowerGSM"
            titleLabel.Font = New Font("Segoe UI", 24, FontStyle.Bold)
            titleLabel.AutoSize = True
            titleLabel.Location = New Point(170, 40)

            Dim subtitleLabel As New Label()
            subtitleLabel.Text = "Game Server Manager"
            subtitleLabel.Font = New Font("Segoe UI", 12)
            subtitleLabel.ForeColor = Color.Gray
            subtitleLabel.AutoSize = True
            subtitleLabel.Location = New Point(175, 85)

            Dim infoLabel As New Label()
            infoLabel.Text = "Select a node or instance from the tree on the left," &
                             vbCrLf & "or use the Nodes menu to add a new node."
            infoLabel.Font = New Font("Segoe UI", 10)
            infoLabel.AutoSize = True
            infoLabel.Location = New Point(22, 170)

            Me.Controls.AddRange(New Control() {_logoBox, titleLabel, subtitleLabel, infoLabel})
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _logoBox IsNot Nothing Then
                Dim img = _logoBox.Image
                _logoBox.Image = Nothing
                If img IsNot Nothing Then
                    Try
                        img.Dispose()
                    Catch
                    End Try
                End If
            End If
            MyBase.Dispose(disposing)
        End Sub

    End Class

    ' ============================================================
    '  NodePanel — shows node status and its installations
    ' ============================================================

    Public Class NodePanel
        Inherits UserControl

        Private ReadOnly _nodeId As String
        Private _nameLabel As Label
        Private _hostLabel As Label
        Private _statusLabel As Label
        Private _compatLabel As Label
        Private _idLabel As Label
        Private _installationsListView As ListView

        ' Cancellation source for the on-load /api/version fetch.
        ' Tripped from Dispose so the async resumption sees the
        ' cancellation and bails out before touching disposed
        ' controls. Belt-and-braces: the resumption ALSO checks
        ' Me.IsDisposed before touching any UI state.
        '
        ' Fully qualified type name (rather than `Imports
        ' System.Threading`) because importing that namespace
        ' makes `Timer` ambiguous with `System.Threading.Timer`
        ' across the rest of this file's WinForms timers.
        Private _versionFetchCts As System.Threading.CancellationTokenSource

        Public Sub New(nodeId As String)
            _nodeId = nodeId
            InitializeControls()
            LoadNodeData()
            ' Fire-and-forget the protocol-version fetch. Constructor
            ' returns immediately; the async method resumes on the UI
            ' SyncContext (captured from this thread) so its label
            ' writes don't need an explicit BeginInvoke marshal.
            _versionFetchCts = New System.Threading.CancellationTokenSource()
            Dim _unused = LoadProtocolVersionAsync(_versionFetchCts.Token)
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _versionFetchCts IsNot Nothing Then
                Try
                    _versionFetchCts.Cancel()
                    _versionFetchCts.Dispose()
                Catch
                End Try
                _versionFetchCts = Nothing
            End If
            MyBase.Dispose(disposing)
        End Sub

        Private Sub InitializeControls()
            _nameLabel = New Label()
            _nameLabel.Font = New Font("Segoe UI", 16, FontStyle.Bold)
            _nameLabel.AutoSize = True
            _nameLabel.Location = New Point(0, 15)

            _hostLabel = New Label()
            _hostLabel.Font = New Font("Segoe UI", 10)
            _hostLabel.ForeColor = Color.Gray
            _hostLabel.AutoSize = True
            _hostLabel.Location = New Point(2, 50)

            _statusLabel = New Label()
            _statusLabel.Font = New Font("Segoe UI", 10)
            _statusLabel.AutoSize = True
            _statusLabel.Location = New Point(2, 75)

            ' Phase 5f-2 — protocol-compatibility line. Sits
            ' between Status and the Installations heading. Starts
            ' as a placeholder "Checking node version..." rendered
            ' in grey; the on-load /api/version fetch fills it in
            ' green/yellow/red based on the comparison against
            ' NodeApiContract.ProtocolVersion. Cap the max width so
            ' a long mismatch message wraps instead of running off
            ' the right edge of the header.
            _compatLabel = New Label()
            _compatLabel.Font = New Font("Segoe UI", 9)
            _compatLabel.AutoSize = True
            _compatLabel.MaximumSize = New Size(700, 0)
            _compatLabel.Location = New Point(2, 98)
            _compatLabel.Text = "Checking node version..."
            _compatLabel.ForeColor = SystemColors.GrayText

            ' Node ID backstop — dim, right-click-copyable. Sits
            ' below the compat line, above the Installations heading.
            _idLabel = PanelIdLabel.Create(New Point(2, 132))
            PanelIdLabel.SetId(_idLabel, _nodeId)

            Dim installLabel As New Label()
            installLabel.Text = "Installations"
            installLabel.Font = New Font("Segoe UI", 11, FontStyle.Bold)
            installLabel.AutoSize = True
            installLabel.Location = New Point(0, 160)

            ' Header section docked to top holds the info labels.
            ' Height bumped from 140 to 160 to accommodate the
            ' compat row and keep the installations heading at
            ' the same visual offset relative to the bottom of
            ' the header section.
            Dim header As New Panel()
            header.Dock = DockStyle.Top
            header.Height = 186
            header.Controls.AddRange(New Control() {
                _nameLabel, _hostLabel, _statusLabel, _compatLabel, _idLabel, installLabel
            })

            _installationsListView = New ListView()
            _installationsListView.Dock = DockStyle.Fill
            _installationsListView.View = View.Details
            _installationsListView.FullRowSelect = True
            _installationsListView.GridLines = True
            _installationsListView.Columns.Add("Name", 200)
            _installationsListView.Columns.Add("Game", 120)
            _installationsListView.Columns.Add("Path", 250)
            _installationsListView.Columns.Add("Version", 100)

            Dim bottomSpacer As New Panel()
            bottomSpacer.Dock = DockStyle.Bottom
            bottomSpacer.Height = 10

            ' Fill first, edge docks after — they reserve their edges
            ' before the fill child claims what remains.
            Me.Controls.Add(_installationsListView)
            Me.Controls.Add(bottomSpacer)
            Me.Controls.Add(header)

            Me.Padding = New Padding(20, 0, 20, 0)
        End Sub

        Private Sub LoadNodeData()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim nodeEntity = db.Nodes.Find(_nodeId)
                If nodeEntity Is Nothing Then
                    _nameLabel.Text = "Node not found"
                    Return
                End If

                _nameLabel.Text = nodeEntity.DisplayName
                _hostLabel.Text = $"{nodeEntity.HostAddress}:{nodeEntity.Port}"
                _statusLabel.Text = If(nodeEntity.IsEnabled, "Attached", "Detached")
                _statusLabel.ForeColor = If(nodeEntity.IsEnabled, Color.DarkGreen, Color.Gray)

                ' Load installations
                Dim installations = db.Installations.
                    Where(Function(i) i.NodeId = _nodeId).
                    ToList()

                _installationsListView.Items.Clear()
                For Each inst In installations
                    Dim item As New ListViewItem(inst.DisplayName)
                    item.SubItems.Add(inst.GameId)
                    item.SubItems.Add(inst.InstallPath)
                    item.SubItems.Add(FormatVersionShort(inst.InstalledVersion))
                    item.Tag = inst.InstallationId
                    _installationsListView.Items.Add(item)
                Next
            End Using
        End Sub

        ''' <summary>
        ''' Fetch the node's /api/version response, compare its
        ''' protocol version against the manager's compiled-in
        ''' value, render the compatibility indicator, and write
        ''' the observed version back to the NodeEntity.
        '''
        ''' Five visual states:
        '''   - In flight: "Checking node version..." (grey)
        '''     — set by InitializeControls before this runs.
        '''   - Connect failure: "Could not contact node" (red).
        '''     Auth/HTTP failures end up here too because the
        '''     unauthenticated /api/version is the easiest call
        '''     to succeed; if it fails, the node is genuinely
        '''     unreachable from the manager's network position.
        '''   - Same protocol: "Protocol v{n} (compatible)" (green).
        '''   - Manager newer: "Manager is newer than node..." (orange).
        '''   - Node newer: "Node is newer than manager..." (orange).
        '''
        ''' Persistence: a successful fetch updates
        ''' NodeEntity.LastSeenProtocolVersion in the DB so other
        ''' consumers (future feature-gating, status panels) can
        ''' read the cached value without a round trip. Failures
        ''' don't clear the column — a transient outage shouldn't
        ''' make us forget what we knew last time.
        ''' </summary>
        Private Async Function LoadProtocolVersionAsync(token As System.Threading.CancellationToken) As Task
            Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
            If factory Is Nothing Then Return

            Dim hostAddress As String = Nothing
            Dim port As Integer = 0
            Dim authToken As String = Nothing
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim nodeEntity = db.Nodes.Find(_nodeId)
                If nodeEntity Is Nothing Then Return
                hostAddress = nodeEntity.HostAddress
                port = nodeEntity.Port
                authToken = nodeEntity.AuthToken
            End Using

            Dim client = factory.GetClient(_nodeId, hostAddress, port, authToken)

            Dim response As NodeVersionResponse = Nothing
            Try
                response = Await client.GetApiVersionAsync(force:=False,
                                                            cancellation:=token)
            Catch ex As OperationCanceledException
                ' Panel disposed mid-flight — nothing to render.
                Return
            Catch ex As Exception
                ' Connection or API failure. Surface as red on the
                ' panel; don't propagate — a transient failure
                ' shouldn't crash the panel host.
            End Try

            If token.IsCancellationRequested OrElse Me.IsDisposed Then Return

            ApplyCompatLabel(response)

            ' Persist back to the DB so a future feature-gating
            ' check (or another panel render) doesn't have to wait
            ' on a round trip. Wrapped in Try — a stale entity row
            ' or DB write failure shouldn't take the panel down.
            If response IsNot Nothing Then
                Try
                    Using scope = ManagerProgram.Services.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim nodeEntity = db.Nodes.Find(_nodeId)
                        If nodeEntity IsNot Nothing AndAlso
                           nodeEntity.LastSeenProtocolVersion <> response.ProtocolVersion Then
                            nodeEntity.LastSeenProtocolVersion = response.ProtocolVersion
                            db.SaveChanges()
                        End If
                    End Using
                Catch
                End Try
            End If
        End Function

        ''' <summary>
        ''' Render the compat indicator from a /api/version
        ''' response (or Nothing on connect failure). Pulled out
        ''' so the rendering logic is unit-testable in spirit
        ''' even if no test harness exists yet.
        ''' </summary>
        Private Sub ApplyCompatLabel(response As NodeVersionResponse)
            If response Is Nothing Then
                _compatLabel.Text = "Could not contact node (connection or version endpoint failure)"
                _compatLabel.ForeColor = Color.Firebrick
                Return
            End If

            Dim managerProtocol = NodeApiContract.ProtocolVersion
            Dim nodeProtocol = response.ProtocolVersion

            If nodeProtocol = managerProtocol Then
                _compatLabel.Text = $"Protocol v{nodeProtocol} (compatible) — node build {SafeBuild(response)}"
                _compatLabel.ForeColor = Color.DarkGreen
            ElseIf managerProtocol > nodeProtocol Then
                ' Treat zero specially: a pre-5f-1 node didn't carry
                ' protocolVersion in /api/version at all, and JSON
                ' deserialised the missing field as 0. Friendlier
                ' to say so explicitly than to render "Node v0".
                If nodeProtocol = 0 Then
                    _compatLabel.Text =
                        "Node is older than this manager (no protocol version reported) — some features may not work."
                Else
                    _compatLabel.Text =
                        $"Node is older than this manager (Manager v{managerProtocol}, Node v{nodeProtocol}) — some features may not work."
                End If
                _compatLabel.ForeColor = Color.DarkOrange
            Else
                _compatLabel.Text =
                    $"Node is newer than this manager (Manager v{managerProtocol}, Node v{nodeProtocol}) — newer node features won't be used."
                _compatLabel.ForeColor = Color.DarkOrange
            End If
        End Sub

        ''' <summary>
        ''' Read the build version off a NodeVersionResponse,
        ''' preferring the new "build" field but falling back to
        ''' the legacy "version" alias for pre-5f-1 nodes that
        ''' only populate the latter. Returns "unknown" only when
        ''' both fields are empty.
        ''' </summary>
        Private Shared Function SafeBuild(response As NodeVersionResponse) As String
            If response Is Nothing Then Return "unknown"
            If Not String.IsNullOrEmpty(response.Build) Then Return response.Build
            If Not String.IsNullOrEmpty(response.Version) Then Return response.Version
            Return "unknown"
        End Function

        ''' <summary>
        ''' Compact version string for the at-a-glance Version column.
        ''' Strips the "steam:{appId}@{branch} build " prefix from
        ''' the full provenance stamp so users see just the buildid
        ''' ("22526048") rather than the entire stamp. Plugin-path
        ''' versions (Factorio's "2.0.42") pass through as-is since
        ''' they're already compact. The InstallationPanel still
        ''' shows the full stamp in the detail view.
        ''' </summary>
        Private Shared Function FormatVersionShort(stamp As String) As String
            If String.IsNullOrEmpty(stamp) Then Return "—"
            Dim buildIdx = stamp.LastIndexOf(" build ")
            If buildIdx > 0 Then
                Return stamp.Substring(buildIdx + " build ".Length).Trim()
            End If
            ' Stamp without a buildid (legacy timestamp-based stamp,
            ' or plugin-path version like "2.0.42"). Return as-is
            ' if it's already short enough; otherwise truncate.
            If stamp.Length <= 24 Then Return stamp
            Return stamp.Substring(0, 21) & "..."
        End Function

    End Class

    ' ============================================================
    '  InstallationPanel — shows installation summary, config,
    '  and its child instances
    ' ============================================================

    Public Class InstallationPanel
        Inherits UserControl

        Private ReadOnly _installationId As String
        Private _nameLabel As Label
        Private _gameLabel As Label
        Private _pathLabel As Label
        Private _methodLabel As Label
        Private _versionLabel As Label
        Private _credentialLabel As Label
        Private _idLabel As Label
        Private _checkUpdatesButton As Button
        Private _updateStatusLabel As Label

        Private _tabs As TabControl
        Private _overviewTab As TabPage
        Private _configTab As TabPage

        Private _instancesList As ListView
        Private _upButton As Button
        Private _downButton As Button
        Private _configContent As Panel

        ' Progress-tab support — fields populated when an
        ' install/update operation is observed via
        ' InstallationManager events. Tab is added on
        ' OperationStarted, never auto-removed (stays until panel
        ' disposed) so the user can continue reading the final
        ' status after completion.
        Private _progressTab As TabPage
        Private _progressView As InstallationProgressView

        ' Cached InstallationManager reference — needed in Dispose
        ' to RemoveHandler with the same delegate target we passed
        ' to AddHandler. Resolved once in the constructor; Nothing
        ' if DI lookup fails (in which case we simply don't get
        ' progress UI — not a fatal condition).
        Private _installationMgr As InstallationManager

        ' Last-selected tab text, persisted across panel disposal +
        ' reconstruction so the user's tab context survives navigation
        ' between installations. Stored by .Text (e.g., "Instances",
        ' "Configuration", "Progress") rather than index because the
        ' Progress tab is dynamic — saved index 2 would mean
        ' "Progress" on an installation with one in flight but be
        ' out of range on one without. Text-keying handles both
        ' cases uniformly and degrades gracefully (unknown text =
        ' default tab) for any other future dynamic additions.
        '
        ' Static (not per-instance) by design: the user's request is
        ' "remember which tab I was on when comparing installations",
        ' not "remember per installation". Manager-restart scope
        ' applies — a fresh manager session starts on the default tab.
        Private Shared _lastSelectedTabText As String

        ' Set during OnLoad's tab-restore path to suppress the
        ' SelectedIndexChanged handler's write-back to
        ' _lastSelectedTabText. Without this, restoring "Configuration"
        ' would trigger the handler which would write "Configuration"
        ' back to the shared static — a no-op in steady state but
        ' a needless write and a source of confusion if a future
        ' refactor adds any logic conditional on the write happening.
        Private _restoringTabSelection As Boolean = False

        Public Sub New(installationId As String)
            _installationId = installationId
            InitializeControls()
            LoadInstallationData()
            ' Subscribe to InstallationManager UI events for
            ' install/update progress on this installation. Done
            ' after InitializeControls so _tabs exists when an
            ' OperationStarted event fires and adds the Progress
            ' tab. Also queries GetActiveProgress synchronously so
            ' a panel opened mid-flight renders current state
            ' without waiting for the next 2s poll tick.
            SubscribeToInstallationManager()
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                ' Unsubscribe BEFORE base Dispose disposes child
                ' controls — otherwise an in-flight event from the
                ' polling thread could land in a handler that
                ' touches a half-disposed control.
                UnsubscribeFromInstallationManager()
            End If
            MyBase.Dispose(disposing)
        End Sub

        ''' <summary>
        ''' Restore the last-selected tab from the class-shared
        ''' static so navigating from one installation to another
        ''' lands the user on the same tab (e.g. flipping between
        ''' installations with Configuration open keeps you on
        ''' Configuration). Falls through silently when the saved
        ''' tab text doesn't match any current tab — typical case is
        ''' the user was on "Progress" while an install was in flight
        ''' and then navigates to an installation with no in-flight
        ''' operation; the new panel just defaults to Instances.
        '''
        ''' Runs in OnLoad rather than the constructor because
        ''' SubscribeToInstallationManager may add the Progress tab
        ''' synchronously when GetActiveProgress returns non-null, and
        ''' we want that potential tab to be findable by name when we
        ''' do the restore lookup. The constructor-then-OnLoad order
        ''' guarantees that.
        ''' </summary>
        Protected Overrides Sub OnLoad(e As EventArgs)
            MyBase.OnLoad(e)

            If Not String.IsNullOrEmpty(_lastSelectedTabText) Then
                Dim target As TabPage = Nothing
                For Each t As TabPage In _tabs.TabPages
                    If String.Equals(t.Text, _lastSelectedTabText, StringComparison.Ordinal) Then
                        target = t
                        Exit For
                    End If
                Next
                If target IsNot Nothing AndAlso target IsNot _tabs.SelectedTab Then
                    _restoringTabSelection = True
                    Try
                        _tabs.SelectedTab = target
                    Finally
                        _restoringTabSelection = False
                    End Try
                End If
            End If
        End Sub

        ''' <summary>
        ''' Tab-change handler that writes the new tab's .Text to the
        ''' class-shared static so the next panel construction can
        ''' restore it. Suppressed during the OnLoad restore so the
        ''' static doesn't get echo-written with what we just read.
        ''' Auto-selects from EnsureProgressTab (user-initiated
        ''' install/update) DO get persisted — if the user is
        ''' watching progress and navigates away, returning to that
        ''' installation should land them back on Progress.
        ''' </summary>
        Private Sub OnTabSelectionChanged(sender As Object, e As EventArgs)
            If _restoringTabSelection Then Return
            Dim tab = _tabs.SelectedTab
            If tab IsNot Nothing AndAlso Not String.IsNullOrEmpty(tab.Text) Then
                _lastSelectedTabText = tab.Text
            End If
        End Sub

        Private Sub InitializeControls()
            _nameLabel = New Label() With {
                .Font = New Font("Segoe UI", 16, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(0, 15)
            }
            _gameLabel = New Label() With {
                .Font = New Font("Segoe UI", 10),
                .ForeColor = Color.Gray,
                .AutoSize = True,
                .Location = New Point(2, 50)
            }
            _pathLabel = New Label() With {
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.DimGray,
                .AutoSize = True,
                .Location = New Point(2, 75)
            }
            ' Install method indicator. Sits between path and
            ' version so users can tell at a glance whether the
            ' install is Steam-managed (and therefore answers to
            ' SteamCMD-based update checks + the Steam-account row
            ' below) or a direct-download install (which uses the
            ' plugin's IVersionAwarePlugin path and has no Steam
            ' credential to show).
            _methodLabel = New Label() With {
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.DimGray,
                .AutoSize = True,
                .Location = New Point(2, 95)
            }
            _versionLabel = New Label() With {
                .Font = New Font("Segoe UI", 9),
                .AutoSize = True,
                .Location = New Point(2, 115)
            }
            _credentialLabel = New Label() With {
                .Font = New Font("Segoe UI", 9),
                .AutoSize = True,
                .Location = New Point(2, 135)
            }

            ' Installation ID backstop — dim, right-click-copyable.
            _idLabel = PanelIdLabel.Create(New Point(2, 153))
            PanelIdLabel.SetId(_idLabel, _installationId)

            ' Check-for-updates button + status label (to the right of
            ' the header info). Hitting the button runs a fast SteamCMD
            ' app_info query on the node — no download.
            _checkUpdatesButton = New Button() With {
                .Text = "Check for Updates",
                .Size = New Size(150, 28),
                .Location = New Point(400, 15)
            }
            AddHandler _checkUpdatesButton.Click, Sub(s, e) OnCheckForUpdates()
            _updateStatusLabel = New Label() With {
                .AutoSize = True,
                .Location = New Point(400, 50),
                .Font = New Font("Segoe UI", 9),
                .MaximumSize = New Size(400, 0)
            }

            Dim header As New Panel()
            header.Dock = DockStyle.Top
            ' Bumped from 150 → 170 to make room for the new
            ' Install Method line. The other rows shift down by
            ' 20px in lockstep so the visual rhythm stays even.
            header.Height = 178
            header.Controls.AddRange(New Control() {
                _nameLabel, _gameLabel, _pathLabel, _methodLabel,
                _versionLabel, _credentialLabel, _idLabel,
                _checkUpdatesButton, _updateStatusLabel
            })

            ' Tabs
            _tabs = New TabControl()
            _tabs.Dock = DockStyle.Fill
            _tabs.Font = New Font("Segoe UI", 9.5F)

            _overviewTab = New TabPage("Instances")
            _configTab = New TabPage("Configuration")

            BuildInstancesTab()
            BuildConfigTab()

            _tabs.TabPages.Add(_overviewTab)
            _tabs.TabPages.Add(_configTab)

            ' Persist user-initiated tab changes to the class-shared
            ' static so the next InstallationPanel constructed (when
            ' the user navigates to a different installation) can
            ' restore the same tab. Hooked AFTER the initial Add calls
            ' above so the synthetic SelectedIndexChanged that fires
            ' when the first tab gets added (SelectedIndex goes from
            ' -1 to 0) doesn't write "Instances" to the static before
            ' the user has actually interacted with anything.
            AddHandler _tabs.SelectedIndexChanged, AddressOf OnTabSelectionChanged

            Dim bottomSpacer As New Panel()
            bottomSpacer.Dock = DockStyle.Bottom
            bottomSpacer.Height = 10

            Me.Controls.Add(_tabs)
            Me.Controls.Add(bottomSpacer)
            Me.Controls.Add(header)
            Me.Padding = New Padding(20, 0, 20, 0)
        End Sub

        Private Sub BuildInstancesTab()
            Dim headerBar As New Panel() With {
                .Dock = DockStyle.Top,
                .Height = 30
            }
            Dim lbl As New Label() With {
                .Text = "Instances on this installation",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(10, 8)
            }
            headerBar.Controls.Add(lbl)

            ' Right-docked button column for reordering. The Up/Down
            ' buttons swap SortOrder values with the adjacent sibling
            ' and persist immediately — there's no "Apply Reorder"
            ' step. Order matters because the stagger feature in
            ' EditInstanceForm uses SortOrder to renumber active
            ' siblings consecutively for offset math.
            Dim buttonCol As New Panel() With {
                .Dock = DockStyle.Right,
                .Width = 90,
                .Padding = New Padding(8, 0, 0, 0)
            }

            _upButton = New Button() With {
                .Text = "▲ Up",
                .Size = New Size(80, 28),
                .Location = New Point(0, 0)
            }
            AddHandler _upButton.Click, Sub(s, ev) OnReorderInstance(-1)
            buttonCol.Controls.Add(_upButton)

            _downButton = New Button() With {
                .Text = "▼ Down",
                .Size = New Size(80, 28),
                .Location = New Point(0, 35)
            }
            AddHandler _downButton.Click, Sub(s, ev) OnReorderInstance(1)
            buttonCol.Controls.Add(_downButton)

            Dim orderHint As New Label() With {
                .Text = "Order affects stagger calculations.",
                .ForeColor = Color.FromArgb(120, 120, 120),
                .Font = New Font("Segoe UI", 8.25F),
                .AutoSize = False,
                .Size = New Size(80, 36),
                .Location = New Point(0, 70)
            }
            buttonCol.Controls.Add(orderHint)

            _instancesList = New ListView()
            _instancesList.Dock = DockStyle.Fill
            _instancesList.View = View.Details
            _instancesList.FullRowSelect = True
            _instancesList.GridLines = True
            _instancesList.HideSelection = False
            _instancesList.Columns.Add("#", 30)
            _instancesList.Columns.Add("Name", 200)
            _instancesList.Columns.Add("Identifier", 160)
            _instancesList.Columns.Add("Auto-start", 80)

            ' Add Fill child first so the Right-docked button column
            ' claims its edge before the listview gets what remains.
            _overviewTab.Controls.Add(_instancesList)
            _overviewTab.Controls.Add(buttonCol)
            _overviewTab.Controls.Add(headerBar)
        End Sub

        Private Sub BuildConfigTab()
            _configContent = New Panel()
            _configContent.Dock = DockStyle.Fill
            _configContent.AutoScroll = True
            _configTab.Controls.Add(_configContent)
        End Sub

        Private Sub LoadInstallationData()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim inst = db.Installations.Find(_installationId)
                If inst Is Nothing Then
                    _nameLabel.Text = "Installation not found"
                    Return
                End If

                _nameLabel.Text = inst.DisplayName
                _gameLabel.Text = $"Game: {inst.GameId}"
                _pathLabel.Text = $"Path: {inst.InstallPath}"
                ApplyMethodLabel(inst)
                ApplyVersionLabel(inst)

                ' Steam credential label — only meaningful for
                ' SteamCmd installs. For DirectDownload / Manual the
                ' credential field doesn't get used at install time
                ' and would mislead the user into thinking the
                ' install logged in to Steam, so we hide the row
                ' entirely. The InstallMethod label above already
                ' tells the user which kind of install they're
                ' looking at; the Steam-account line just adds noise
                ' for non-Steam installs.
                Dim isSteamInstall = String.Equals(inst.InstallMethod,
                                                    InstallMethod.SteamCmd.ToString(),
                                                    StringComparison.OrdinalIgnoreCase)
                If Not isSteamInstall Then
                    _credentialLabel.Visible = False
                Else
                    _credentialLabel.Visible = True
                    ' Use reflection so we don't hard-bind to a
                    ' specific property name on the entity (could be
                    ' AccountName, Username, Login, etc.).
                    If Not String.IsNullOrEmpty(inst.SteamCredentialId) Then
                        Try
                            Dim cred = db.SteamCredentials.Find(inst.SteamCredentialId)
                            If cred IsNot Nothing Then
                                Dim acctName = GetStringProperty(cred,
                                    {"Username", "UserName", "AccountName", "Login", "DisplayName"})
                                If String.IsNullOrEmpty(acctName) Then acctName = "(assigned)"
                                _credentialLabel.Text = $"Steam account: {acctName}"
                                _credentialLabel.ForeColor = Color.DarkGreen
                            Else
                                _credentialLabel.Text = "Steam account: (credential missing)"
                                _credentialLabel.ForeColor = Color.Firebrick
                            End If
                        Catch
                            _credentialLabel.Text = "Steam account: (assigned)"
                            _credentialLabel.ForeColor = Color.DarkGreen
                        End Try
                    Else
                        _credentialLabel.Text = "Steam account: anonymous (default)"
                        _credentialLabel.ForeColor = Color.Gray
                    End If
                End If

                ' Load child instances ordered by SortOrder so the
                ' display matches the stagger-calculation ordering.
                ' CreatedUtc as the secondary key keeps the order
                ' stable when SortOrder is duplicated (legacy data).
                Dim instances = db.Instances.
                    Where(Function(i) i.InstallationId = _installationId).
                    OrderBy(Function(i) i.SortOrder).
                    ThenBy(Function(i) i.CreatedUtc).
                    ToList()

                _instancesList.Items.Clear()
                Dim displayPos = 1
                For Each i In instances
                    ' Position column — 1-based renumbered for the
                    ' user, independent of the actual SortOrder values
                    ' (which can have gaps after reorders). Same as
                    ' how the stagger algorithm renumbers internally.
                    Dim item As New ListViewItem(displayPos.ToString())
                    item.SubItems.Add(i.DisplayName)
                    ' Parse Identifier out of ConfigJson if present
                    Dim identifier = ""
                    If Not String.IsNullOrEmpty(i.ConfigJson) Then
                        Try
                            Dim dict = System.Text.Json.JsonSerializer.Deserialize(
                                Of Dictionary(Of String, String))(i.ConfigJson)
                            If dict IsNot Nothing AndAlso dict.ContainsKey("Identifier") Then
                                identifier = dict("Identifier")
                            End If
                        Catch
                        End Try
                    End If
                    item.SubItems.Add(identifier)
                    item.SubItems.Add(If(i.AutoStart, "Yes", "No"))
                    item.Tag = i.InstanceId
                    _instancesList.Items.Add(item)
                    displayPos += 1
                Next

                ' Populate the installation config schema (read-only)
                PopulateConfigTab(inst)
            End Using
        End Sub

        ''' <summary>
        ''' Render the install-method indicator ("Install method:
        ''' Steam (SteamCMD)" / "Install method: Direct download" /
        ''' "Install method: Manual"). Lives on its own header line
        ''' so the user can tell SteamCmd from non-SteamCmd installs
        ''' at a glance — previously the only signal was the
        ''' presence of the Steam-account row, which is implicit
        ''' enough that users didn't pick up on it. The label
        ''' renders verbatim for unknown method strings rather than
        ''' hiding, so a future install method (or a manually-edited
        ''' DB row) is still visible.
        ''' </summary>
        Private Sub ApplyMethodLabel(inst As InstallationEntity)
            Dim raw = If(inst.InstallMethod, "")
            Dim parsed As InstallMethod
            If Not [Enum].TryParse(raw, True, parsed) Then
                _methodLabel.Text = $"Install method: {raw}"
                Return
            End If
            Select Case parsed
                Case InstallMethod.SteamCmd
                    _methodLabel.Text = "Install method: Steam (SteamCMD)"
                Case InstallMethod.DirectDownload
                    _methodLabel.Text = "Install method: Direct download"
                Case InstallMethod.Manual
                    _methodLabel.Text = "Install method: Manual"
                Case Else
                    _methodLabel.Text = $"Install method: {raw}"
            End Select
        End Sub

        ''' <summary>
        ''' Renders the Version: line with installed/latest values
        ''' and a "checked Nm ago" suffix. Phase 5: replaces the
        ''' previous InstalledVersion-only display so users see
        ''' both the installed version and what the upstream is at.
        '''
        ''' Color coding (subtle — the orange "update available"
        ''' state is the only one users need to act on):
        '''   - Update available  → dark orange
        '''   - Up to date        → default (black)
        '''   - Not yet installed → gray
        '''   - Not yet checked   → black with parenthetical hint
        ''' </summary>
        Private Sub ApplyVersionLabel(inst As InstallationEntity)
            If String.IsNullOrEmpty(inst.InstalledVersion) Then
                _versionLabel.Text = "Version: (not yet installed)"
                _versionLabel.ForeColor = Color.DarkGray
                Return
            End If

            Dim ageSuffix As String = ""
            If inst.LastVersionCheckUtc.HasValue Then
                Dim ago = DateTime.UtcNow - inst.LastVersionCheckUtc.Value
                ' "Last successfully checked" rather than just
                ' "checked" because LastVersionCheckUtc is only
                ' written on a successful version check (see the
                ' header comment in VersionCheckService) — a failed
                ' check leaves the timestamp untouched. Saying
                ' "successfully" makes the semantics explicit so
                ' a user looking at a stale timestamp on a node
                ' that's been failing checks for hours knows the
                ' value isn't lying about the failure, just about
                ' the last good result.
                ageSuffix = $", last successfully checked {FormatVersionAgo(ago)}"
            End If

            ' No latest known yet — show installed only, plus a
            ' parenthetical noting the version-check service hasn't
            ' run for this installation yet (or hasn't succeeded).
            If String.IsNullOrEmpty(inst.LatestKnownVersion) Then
                If String.IsNullOrEmpty(ageSuffix) Then
                    _versionLabel.Text = $"Version: {inst.InstalledVersion} (not yet checked)"
                Else
                    _versionLabel.Text = $"Version: {inst.InstalledVersion} (not yet checked{ageSuffix})"
                End If
                _versionLabel.ForeColor = SystemColors.ControlText
                Return
            End If

            ' Both values present — compare and render accordingly.
            If String.Equals(inst.InstalledVersion, inst.LatestKnownVersion,
                              StringComparison.Ordinal) Then
                _versionLabel.Text = $"Version: {inst.InstalledVersion} (up to date{ageSuffix})"
                _versionLabel.ForeColor = SystemColors.ControlText
            Else
                _versionLabel.Text = $"Version: {inst.InstalledVersion} → {inst.LatestKnownVersion} (update available{ageSuffix})"
                _versionLabel.ForeColor = Color.DarkOrange
            End If
        End Sub

        ''' <summary>
        ''' Compact "how long ago" string for the version-check
        ''' timestamp. Mirrors the FormatBriefAgo helper in the
        ''' AutomationRulesForm but inlined here so we don't have
        ''' to expose it cross-file.
        ''' </summary>
        Private Shared Function FormatVersionAgo(span As TimeSpan) As String
            If span.TotalSeconds < 5 Then Return "just now"
            If span.TotalSeconds < 60 Then Return $"{CInt(span.TotalSeconds)}s ago"
            If span.TotalMinutes < 60 Then Return $"{CInt(Math.Floor(span.TotalMinutes))}m ago"
            If span.TotalHours < 24 Then Return $"{CInt(Math.Floor(span.TotalHours))}h ago"
            Return $"{CInt(Math.Floor(span.TotalDays))}d ago"
        End Function

        Private Sub PopulateConfigTab(installEntity As InstallationEntity)
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return
                Dim plugin = registry.GetPlugin(installEntity.GameId)
                If plugin Is Nothing Then
                    Dim warn As New Label() With {
                        .Text = $"Plugin '{installEntity.GameId}' is not loaded.",
                        .AutoSize = True,
                        .Location = New Point(10, 10),
                        .ForeColor = Color.Firebrick
                    }
                    _configContent.Controls.Add(warn)
                    Return
                End If

                Dim existing As New Dictionary(Of String, String)
                If Not String.IsNullOrEmpty(installEntity.ConfigJson) Then
                    Try
                        existing = System.Text.Json.JsonSerializer.Deserialize(
                            Of Dictionary(Of String, String))(installEntity.ConfigJson)
                        If existing Is Nothing Then existing = New Dictionary(Of String, String)
                    Catch
                    End Try
                End If

                Dim schema = plugin.GetInstallConfigSchema()
                Dim result = SchemaFormBuilder.Build(schema, existing)

                Dim hint As New Label() With {
                    .Text = "(Read-only view — use right-click → Edit Installation... to change)",
                    .AutoSize = True,
                    .Location = New Point(10, 8),
                    .ForeColor = Color.Gray,
                    .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic)
                }
                _configContent.Controls.Add(hint)

                If result.Panel IsNot Nothing Then
                    Const HeaderOffset As Integer = 35
                    Dim children = result.Panel.Controls.Cast(Of Control).ToArray()
                    For Each child In children
                        result.Panel.Controls.Remove(child)
                        child.Location = New Point(child.Location.X,
                                                    child.Location.Y + HeaderOffset)
                        _configContent.Controls.Add(child)
                    Next
                    DisableControls(_configContent)
                End If
            Catch ex As Exception
                Dim err As New Label() With {
                    .Text = $"Failed to load configuration: {ex.Message}",
                    .AutoSize = True,
                    .Location = New Point(10, 10),
                    .ForeColor = Color.Firebrick
                }
                _configContent.Controls.Add(err)
            End Try
        End Sub

        Private Function GetStringProperty(obj As Object, candidateNames As String()) As String
            If obj Is Nothing Then Return Nothing
            Dim t = obj.GetType()
            For Each propName In candidateNames
                Dim prop = t.GetProperty(propName)
                If prop IsNot Nothing AndAlso prop.CanRead Then
                    Dim val = prop.GetValue(obj)
                    If val IsNot Nothing Then Return val.ToString()
                End If
            Next
            Return Nothing
        End Function

        Private Sub DisableControls(parent As Control)
            For Each child As Control In parent.Controls
                Dim tb = TryCast(child, TextBox)
                If tb IsNot Nothing Then
                    tb.ReadOnly = True
                    tb.BackColor = SystemColors.Control
                    Continue For
                End If
                Dim cb = TryCast(child, ComboBox)
                If cb IsNot Nothing Then
                    cb.Enabled = False
                    Continue For
                End If
                Dim chk = TryCast(child, CheckBox)
                If chk IsNot Nothing Then
                    chk.AutoCheck = False
                    Continue For
                End If
                Dim nud = TryCast(child, NumericUpDown)
                If nud IsNot Nothing Then
                    nud.ReadOnly = True
                    nud.Increment = 0
                    Continue For
                End If
                If child.HasChildren Then DisableControls(child)
            Next
        End Sub

        ''' <summary>
        ''' Move the selected instance up (-1) or down (+1) in the
        ''' SortOrder. Implemented as a swap with the adjacent
        ''' sibling so SortOrder values stay consecutive without
        ''' renumbering the whole installation — cheaper write,
        ''' less risk of a partial-update mid-failure.
        '''
        ''' UI update strategy: in-place item swap rather than a
        ''' full LoadInstallationData() rebuild. Earlier rebuild-
        ''' based attempts kept losing the selection through some
        ''' focus cascade we never fully diagnosed — possibly the
        ''' Items.Clear() interacting with the button's focus state.
        ''' Swapping items in place keeps the listview's state
        ''' graph entirely intact: the moved row's ListViewItem
        ''' object stays the same instance, just at a new index,
        ''' and we only need to reassign Selected on it.
        ''' </summary>
        Private Sub OnReorderInstance(direction As Integer)
            If _instancesList.SelectedItems.Count = 0 Then Return
            Dim selectedIdx = _instancesList.SelectedItems(0).Index
            Dim newIdx = selectedIdx + direction
            If newIdx < 0 OrElse newIdx >= _instancesList.Items.Count Then Return

            Dim selectedId = _instancesList.SelectedItems(0).Tag.ToString()
            Dim swapId = _instancesList.Items(newIdx).Tag.ToString()

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim a = db.Instances.Find(selectedId)
                Dim b = db.Instances.Find(swapId)
                If a Is Nothing OrElse b Is Nothing Then Return

                ' Swap the SortOrder values. If the values happen to
                ' be equal (legacy data), nudge them apart so the
                ' next reorder behaves predictably.
                If a.SortOrder = b.SortOrder Then
                    a.SortOrder = b.SortOrder + direction
                Else
                    Dim tmp = a.SortOrder
                    a.SortOrder = b.SortOrder
                    b.SortOrder = tmp
                End If
                a.UpdatedUtc = DateTime.UtcNow
                b.UpdatedUtc = DateTime.UtcNow
                db.SaveChanges()
            End Using

            ' UI update: swap CONTENT (text + tag) between the two
            ' rows rather than moving ListViewItem objects. The Win32
            ' listview's selection state is keyed on row INDEX, so
            ' moving items by remove/reinsert was confusing the
            ' selection rendering. Swapping content keeps both row
            ' objects in place and only their data changes.
            '
            ' After the swap, we move the selection to the row that
            ' now contains the user's originally-selected data —
            ' i.e. selection follows the data, not the row index.
            _instancesList.BeginUpdate()
            Try
                Dim rowA = _instancesList.Items(selectedIdx)
                Dim rowB = _instancesList.Items(newIdx)

                ' Swap all subitem texts (skipping column 0, the #
                ' column, which represents row position not data).
                For col = 1 To Math.Min(rowA.SubItems.Count, rowB.SubItems.Count) - 1
                    Dim tmp = rowA.SubItems(col).Text
                    rowA.SubItems(col).Text = rowB.SubItems(col).Text
                    rowB.SubItems(col).Text = tmp
                Next

                ' Swap tags too — these store the InstanceId, used
                ' by the next click to identify which row's data is
                ' currently "the selected instance".
                Dim tmpTag = rowA.Tag
                rowA.Tag = rowB.Tag
                rowB.Tag = tmpTag

                ' The # column stays as-is on each row (row 0 is
                ' always "1", row 1 is always "2", etc.), so no
                ' renumbering needed.

                ' Move selection to the row that now contains the
                ' user's original data (newIdx). Clear the old
                ' selection first to avoid both being selected
                ' transiently.
                rowA.Selected = False
                rowB.Selected = True
                rowB.EnsureVisible()
            Finally
                _instancesList.EndUpdate()
            End Try

            ' Refresh the main tree so its Instance children visually
            ' reflect the new order. The tree's selection-restore code
            ' suppresses AfterSelect during the rebuild so this won't
            ' destroy our just-set listview selection (we lost an hour
            ' to that bug — see _suppressTreeAfterSelect in MainForm).
            Dim mainForm = Application.OpenForms.OfType(Of MainForm)().FirstOrDefault()
            mainForm?.RefreshNodeTree()
        End Sub

        ''' <summary>
        ''' Click handler for the "Check for Updates" button. Phase 5:
        ''' routes through VersionCheckService.CheckInstallationAsync
        ''' rather than calling InstallationManager.CheckForUpdatesAsync
        ''' directly. This way the same code path covers both Steam-
        ''' installed games (where the service delegates to
        ''' InstallationManager.CheckForUpdatesAsync internally) AND
        ''' plugin-driven version checks (Factorio, future games
        ''' implementing IVersionAwarePlugin).
        '''
        ''' Side effect: a successful check updates the entity's
        ''' LatestKnownVersion + LastVersionCheckUtc columns and
        ''' raises an automation event if a new mismatch is detected.
        ''' We refresh the panel afterwards so the version label
        ''' picks up the new values immediately.
        '''
        ''' respectThrottle:=False bypasses the 55-minute restart-
        ''' grace window so the manual button always actually checks.
        ''' </summary>
        Private Async Sub OnCheckForUpdates()
            _checkUpdatesButton.Enabled = False
            _updateStatusLabel.Text = "Checking..."
            _updateStatusLabel.ForeColor = Color.DarkOrange
            Try
                Dim svc = ManagerProgram.Services.GetService(Of VersionCheckService)()
                If svc Is Nothing Then
                    _updateStatusLabel.Text = "VersionCheckService not registered"
                    _updateStatusLabel.ForeColor = Color.Firebrick
                    Return
                End If

                Dim result = Await svc.CheckInstallationAsync(
                    _installationId,
                    respectThrottle:=False,
                    cancellation:=System.Threading.CancellationToken.None)
                If Me.IsDisposed Then Return

                ' Re-read the entity to pick up the LatestKnownVersion /
                ' LastVersionCheckUtc the service just wrote, and refresh
                ' the version label inline.
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim inst = db.Installations.Find(_installationId)
                    If inst IsNot Nothing Then
                        ApplyVersionLabel(inst)
                        If result.Success Then
                            ' Surface a status message that mirrors the
                            ' version-label state so the user gets clear
                            ' "check completed" feedback even though the
                            ' main signal moved into the version label.
                            If String.IsNullOrEmpty(inst.LatestKnownVersion) Then
                                _updateStatusLabel.Text = "Check returned no version (transient failure or unsupported plugin)"
                                _updateStatusLabel.ForeColor = Color.DarkGray
                            ElseIf String.Equals(inst.InstalledVersion, inst.LatestKnownVersion,
                                                   StringComparison.Ordinal) Then
                                _updateStatusLabel.Text = $"Up to date ({inst.LatestKnownVersion})"
                                _updateStatusLabel.ForeColor = Color.DarkGreen
                            Else
                                _updateStatusLabel.Text =
                                    $"Update available: {inst.InstalledVersion} → {inst.LatestKnownVersion}"
                                _updateStatusLabel.ForeColor = Color.DarkOrange
                            End If
                        Else
                            ShowCheckUpdateFailure(result.ErrorMessage)
                        End If
                    End If
                End Using
            Catch ex As Exception
                If Me.IsDisposed Then Return
                ShowCheckUpdateFailure(ex.Message)
            Finally
                _checkUpdatesButton.Enabled = True
            End Try
        End Sub

        ''' <summary>
        ''' Render a check-for-updates failure to the user. Short
        ''' messages stay in the status label below the button so
        ''' the result is visible at a glance; long or multi-line
        ''' messages (e.g. the SteamCMD missing-libs hint that ships
        ''' with the Linux pre-flight, ~500 chars across multiple
        ''' lines) open in a resizable monospace dialog so the user
        ''' can read the full diagnostic and copy it. The label
        ''' still shows a short one-line summary in that case so
        ''' the post-dismiss state is still informative.
        '''
        ''' Threshold: > 150 chars OR contains a newline. Both
        ''' signals correlate well with "doesn't fit in a 400px-
        ''' wide AutoSize label" in practice; using either-or
        ''' catches the cases where one is true but not the other
        ''' (a 1200-char single-line stack trace, a short 3-line
        ''' formatted hint).
        ''' </summary>
        Private Sub ShowCheckUpdateFailure(errMessage As String)
            Dim msg = If(String.IsNullOrEmpty(errMessage),
                          "Check failed (no error message reported).",
                          errMessage)

            Dim isLong = msg.Length > 150 OrElse
                         msg.IndexOf(vbLf) >= 0 OrElse
                         msg.IndexOf(vbCr) >= 0

            If isLong Then
                ' Modal dialog with the full text. Owner is the
                ' host form so the dialog centres correctly and
                ' Alt+Tab grouping reads sanely. Wrapped in Try
                ' so a dialog-open failure (rare — disposed parent,
                ' WindowStation issue) doesn't suppress the label
                ' fallback below.
                Try
                    DetailedErrorDialog.Show(Me.FindForm(),
                        "Check for Updates Failed",
                        "The version check failed.",
                        msg)
                Catch
                End Try

                ' Short summary on the label: first non-empty line,
                ' truncated to fit. Gives the user the signal
                ' without the full wall of text after they've
                ' closed the dialog.
                Dim firstLine = msg.Split({vbCr, vbLf},
                                            StringSplitOptions.RemoveEmptyEntries).
                                  FirstOrDefault()
                If String.IsNullOrEmpty(firstLine) Then
                    firstLine = "see dialog for details"
                End If
                If firstLine.Length > 130 Then
                    firstLine = firstLine.Substring(0, 127) & "..."
                End If
                _updateStatusLabel.Text = "Check failed: " & firstLine
            Else
                ' Short message — fits in the label, no dialog
                ' needed. Same surface as the previous "Check
                ' failed: {ex.Message}" path used before the
                ' result-object refactor.
                _updateStatusLabel.Text = "Check failed: " & msg
            End If
            _updateStatusLabel.ForeColor = Color.Firebrick
        End Sub

        ' ============================================================
        '  Install/update progress event subscription
        ' ============================================================

        ''' <summary>
        ''' Resolve the InstallationManager from DI, render any
        ''' already-in-flight operation, and subscribe to the three
        ''' lifecycle events for future updates. Called once from
        ''' the constructor.
        '''
        ''' Order matters: GetActiveProgress is queried BEFORE
        ''' AddHandler so we don't double-render the initial state
        ''' (the AddHandler subscription doesn't replay missed events,
        ''' so a synchronous initial query covers the gap between
        ''' "op started" and "panel constructed").
        ''' </summary>
        Private Sub SubscribeToInstallationManager()
            Try
                _installationMgr = ManagerProgram.Services.GetService(Of InstallationManager)()
            Catch
                _installationMgr = Nothing
            End Try
            If _installationMgr Is Nothing Then Return

            ' Render any operation already in flight on this
            ' installation. We don't know IsUpdate from the
            ' progress snapshot alone (only OperationStarted carries
            ' that flag), so default to False here — the title shows
            ' "Operation in progress..." generically and the phase
            ' label tells the user what's actually happening. Don't
            ' auto-select the tab on this path: the user navigated
            ' here themselves, they get to choose whether to look at
            ' Progress or stay on Instances.
            Try
                Dim active = _installationMgr.GetActiveProgress(_installationId)
                If active IsNot Nothing Then
                    EnsureProgressTab(isUpdate:=False, autoSelect:=False)
                    If _progressView IsNot Nothing Then
                        _progressView.UpdateProgress(active)
                    End If
                End If
            Catch
                ' Best-effort rendering of initial state — if it
                ' fails the events will catch up on the next tick.
            End Try

            AddHandler _installationMgr.OperationStarted, AddressOf OnInstallationOperationStarted
            AddHandler _installationMgr.ProgressChanged, AddressOf OnInstallationProgressChanged
            AddHandler _installationMgr.OperationCompleted, AddressOf OnInstallationOperationCompleted
        End Sub

        Private Sub UnsubscribeFromInstallationManager()
            If _installationMgr Is Nothing Then Return
            Try
                RemoveHandler _installationMgr.OperationStarted, AddressOf OnInstallationOperationStarted
                RemoveHandler _installationMgr.ProgressChanged, AddressOf OnInstallationProgressChanged
                RemoveHandler _installationMgr.OperationCompleted, AddressOf OnInstallationOperationCompleted
            Catch
                ' RemoveHandler is forgiving of unsubscribed delegates;
                ' this catch is for any unexpected manager-side error.
            End Try
            _installationMgr = Nothing
        End Sub

        ' ---- Event handlers ----
        '
        ' Each handler does the same dance: filter on installationId,
        ' marshal to UI thread, then delegate to an Apply* method that
        ' assumes UI-thread context. Keeping the dispatch and the
        ' work in separate methods makes the apply side trivial to
        ' read without the threading boilerplate cluttering it.

        Private Sub OnInstallationOperationStarted(sender As Object, e As InstallationOperationStartedEventArgs)
            If e Is Nothing OrElse Not String.Equals(e.InstallationId, _installationId, StringComparison.Ordinal) Then Return
            Try
                If Me.IsDisposed Then Return
                If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                    Me.BeginInvoke(New Action(Sub() ApplyOperationStarted(e)))
                Else
                    ApplyOperationStarted(e)
                End If
            Catch
                ' BeginInvoke can race with handle destruction — swallow.
            End Try
        End Sub

        Private Sub OnInstallationProgressChanged(sender As Object, e As InstallationProgressEventArgs)
            If e Is Nothing OrElse Not String.Equals(e.InstallationId, _installationId, StringComparison.Ordinal) Then Return
            Try
                If Me.IsDisposed Then Return
                If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                    Me.BeginInvoke(New Action(Sub() ApplyProgressChanged(e)))
                Else
                    ApplyProgressChanged(e)
                End If
            Catch
            End Try
        End Sub

        Private Sub OnInstallationOperationCompleted(sender As Object, e As InstallationOperationCompletedEventArgs)
            If e Is Nothing OrElse Not String.Equals(e.InstallationId, _installationId, StringComparison.Ordinal) Then Return
            Try
                If Me.IsDisposed Then Return
                If Me.IsHandleCreated AndAlso Me.InvokeRequired Then
                    Me.BeginInvoke(New Action(Sub() ApplyOperationCompleted(e)))
                Else
                    ApplyOperationCompleted(e)
                End If
            Catch
            End Try
        End Sub

        Private Sub ApplyOperationStarted(e As InstallationOperationStartedEventArgs)
            If Me.IsDisposed Then Return
            EnsureProgressTab(isUpdate:=e.IsUpdate, autoSelect:=e.UserInitiated)
        End Sub

        Private Sub ApplyProgressChanged(e As InstallationProgressEventArgs)
            If Me.IsDisposed Then Return
            If _progressView IsNot Nothing AndAlso Not _progressView.IsDisposed Then
                _progressView.UpdateProgress(e.Progress)
            End If
        End Sub

        Private Sub ApplyOperationCompleted(e As InstallationOperationCompletedEventArgs)
            If Me.IsDisposed Then Return
            If _progressView IsNot Nothing AndAlso Not _progressView.IsDisposed Then
                _progressView.ShowCompletion(e.Success, e.ErrorMessage)
            End If
            ' On successful completion the InstalledVersion field
            ' on the entity has been updated by ExecuteInstallInternal
            ' — reload the header version label to pick that up
            ' without forcing the user to navigate away and back.
            If e.Success Then
                Try
                    Using scope = ManagerProgram.Services.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim inst = db.Installations.Find(_installationId)
                        If inst IsNot Nothing Then ApplyVersionLabel(inst)
                    End Using
                Catch
                End Try
            End If
        End Sub

        ''' <summary>
        ''' Add the Progress tab to _tabs if not already present, and
        ''' optionally select it. Idempotent — a second call after
        ''' the tab exists just sets the operation kind on the
        ''' existing view (in case the kind changed, e.g. an aborted
        ''' install retried as an update).
        ''' </summary>
        Private Sub EnsureProgressTab(isUpdate As Boolean, autoSelect As Boolean)
            If _progressTab Is Nothing Then
                _progressView = New InstallationProgressView()
                _progressView.Dock = DockStyle.Fill

                _progressTab = New TabPage("Progress")
                _progressTab.Controls.Add(_progressView)

                If _tabs IsNot Nothing Then _tabs.TabPages.Add(_progressTab)
            End If

            _progressView.SetOperationKind(isUpdate)

            If autoSelect AndAlso _tabs IsNot Nothing AndAlso _progressTab IsNot Nothing Then
                Try
                    _tabs.SelectedTab = _progressTab
                Catch
                    ' SelectedTab can throw if the tab control isn't
                    ' fully initialized yet (rare) — swallow.
                End Try
            End If
        End Sub

    End Class

    ' ============================================================
    '  InstallationProgressView — inline progress display for
    '  install/update operations on the InstallationPanel
    ' ============================================================

    ''' <summary>
    ''' Read-only view of an in-flight or recently-completed
    ''' install/update. Hosted in the InstallationPanel's Progress
    ''' tab, populated by InstallationPanel via the InstallationManager
    ''' UI events. Three public mutators:
    '''
    '''   - SetOperationKind: called from OperationStarted with the
    '''     IsUpdate flag, sets the title ("Installing..." vs
    '''     "Updating...").
    '''   - UpdateProgress: called on each ProgressChanged tick,
    '''     refreshes the phase label, step label, progress bar, and
    '''     status message from the InstallProgressResponse.
    '''   - ShowCompletion: called on OperationCompleted, swaps the
    '''     in-progress UI for a success/failure summary.
    '''
    ''' All mutators must be called on the UI thread — that's the
    ''' caller's responsibility (the InstallationPanel handles
    ''' marshaling via Control.BeginInvoke before invoking these).
    ''' </summary>
    Public Class InstallationProgressView
        Inherits UserControl

        Private _titleLabel As Label
        Private _phaseLabel As Label
        Private _stepLabel As Label
        Private _progressBar As ProgressBar
        Private _statusLabel As Label
        Private _resultLabel As Label

        Public Sub New()
            InitializeControls()
        End Sub

        Private Sub InitializeControls()
            _titleLabel = New Label() With {
                .Font = New Font("Segoe UI", 14, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(10, 12),
                .Text = "Operation in progress..."
            }

            _phaseLabel = New Label() With {
                .Font = New Font("Segoe UI", 11),
                .AutoSize = True,
                .Location = New Point(10, 50),
                .Text = "Phase: —"
            }

            _stepLabel = New Label() With {
                .Font = New Font("Segoe UI", 9.5F),
                .ForeColor = Color.Gray,
                .AutoSize = True,
                .Location = New Point(10, 80),
                .MaximumSize = New Size(700, 0)
            }

            ' Fixed width with Top|Left anchor only — Padding on a
            ' UserControl doesn't reserve space for anchored children
            ' (anchors snap to the actual control edge, not the padded
            ' inset), so a Right-anchored bar pinned to the panel edge
            ' reads as cut off. Hardcoded width matches how most
            ' desktop installers render their progress bars and gives
            ' clear breathing room on the right regardless of how wide
            ' the host panel gets.
            _progressBar = New ProgressBar() With {
                .Location = New Point(10, 110),
                .Size = New Size(480, 22),
                .Minimum = 0,
                .Maximum = 100,
                .Style = ProgressBarStyle.Continuous,
                .Anchor = AnchorStyles.Top Or AnchorStyles.Left
            }

            _statusLabel = New Label() With {
                .Font = New Font("Segoe UI", 9.5F),
                .AutoSize = True,
                .Location = New Point(10, 144),
                .MaximumSize = New Size(700, 0)
            }

            ' Result label hidden until OperationCompleted fires.
            ' MaximumSize bounded so a long error message wraps
            ' inside the panel rather than running off-screen.
            _resultLabel = New Label() With {
                .Font = New Font("Segoe UI", 11, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(10, 180),
                .Visible = False,
                .MaximumSize = New Size(700, 0)
            }

            Me.Controls.AddRange(New Control() {
                _titleLabel, _phaseLabel, _stepLabel,
                _progressBar, _statusLabel, _resultLabel
            })
            Me.Padding = New Padding(10, 10, 10, 10)
        End Sub

        ''' <summary>
        ''' Set the title bar text based on whether the operation is
        ''' a fresh install or an update. Idempotent; safe to call
        ''' multiple times. No-op once ShowCompletion has fired — the
        ''' result-state title takes precedence over the in-progress
        ''' one.
        ''' </summary>
        Public Sub SetOperationKind(isUpdate As Boolean)
            If _resultLabel IsNot Nothing AndAlso _resultLabel.Visible Then Return
            _titleLabel.Text = If(isUpdate, "Updating...", "Installing...")
        End Sub

        Public Sub UpdateProgress(progress As InstallProgressResponse)
            If progress Is Nothing Then Return

            ' Phase: prefer the SteamCMD phase string when present
            ' ("Reconfiguring" / "Verifying" / "Preallocating" /
            ' "Downloading" / "Committing"), fall back to the broader
            ' OperationState enum ("Queued" / "Downloading" /
            ' "WaitingForInput") when SteamCMD hasn't surfaced a
            ' phase yet (early start, non-Steam install methods).
            Dim phase = If(progress.SteamCmdPhase, "")
            If String.IsNullOrEmpty(phase) Then
                phase = progress.OperationState.ToString()
            End If
            _phaseLabel.Text = $"Phase: {phase}"

            ' Step indicator. CurrentStepIndex is 0-based; show as
            ' 1-based to match user expectations. Suppress entirely
            ' for single-step operations to keep the UI clean.
            If progress.TotalSteps > 1 Then
                Dim stepName = If(progress.CurrentStepName, "")
                _stepLabel.Text = $"Step {progress.CurrentStepIndex + 1} of {progress.TotalSteps}: {stepName}"
            ElseIf Not String.IsNullOrEmpty(progress.CurrentStepName) Then
                _stepLabel.Text = progress.CurrentStepName
            Else
                _stepLabel.Text = ""
            End If

            ' Progress bar. Clamp to 0..100; the source is a Double
            ' so out-of-range values are theoretically possible.
            Dim pct = CInt(Math.Floor(progress.ProgressPercent))
            If pct < 0 Then pct = 0
            If pct > 100 Then pct = 100
            _progressBar.Value = pct

            _statusLabel.Text = If(progress.Message, "")
        End Sub

        ''' <summary>
        ''' Final-state display. Replaces the in-progress title and
        ''' phase, fills the bar to 100% on success (already there
        ''' if the operation reached 99%, but normalises the visual
        ''' on faster paths that completed before a 99% read), and
        ''' shows the result label with a bold green/red message.
        ''' </summary>
        Public Sub ShowCompletion(success As Boolean, errorMessage As String)
            If success Then
                _titleLabel.Text = "Completed"
                _phaseLabel.Text = "Phase: Completed"
                _phaseLabel.ForeColor = Color.DarkGreen
                _progressBar.Value = 100
                _resultLabel.Text = "✓ Completed successfully"
                _resultLabel.ForeColor = Color.DarkGreen
            Else
                _titleLabel.Text = "Failed"
                _phaseLabel.Text = "Phase: Failed"
                _phaseLabel.ForeColor = Color.Firebrick
                _resultLabel.Text = If(String.IsNullOrEmpty(errorMessage),
                                         "✗ Failed (no error message reported)",
                                         $"✗ Failed: {errorMessage}")
                _resultLabel.ForeColor = Color.Firebrick
            End If
            _resultLabel.Visible = True
        End Sub
    End Class

    ' ============================================================
    '  InstancePanel — shows instance status and controls
    ' ============================================================

    Public Class InstancePanel
        Inherits UserControl

        Private ReadOnly _instanceId As String
        Private _nameLabel As Label
        Private _gameLabel As Label
        Private _statusLabel As Label
        Private _idLabel As Label
        Private _startButton As Button
        Private _stopButton As Button
        Private _restartButton As Button
        Private _showLogsToggle As CheckBox
        Private _historyButton As Button

        ' Tab hosts
        Private _tabs As TabControl
        Private _overviewTab As TabPage
        Private _configTab As TabPage
        Private _chatTab As TabPage

        ' Logs tab — created lazily when _showLogsToggle is toggled on,
        ' destroyed when toggled off. Polling timer is started/stopped
        ' with the tab so users who aren't watching logs don't pay the
        ' polling cost or the manager-buffer drain it requires.
        Private _logsTab As TabPage
        Private _logTextBox As RichTextBox
        Private _logAutoScrollCheckBox As CheckBox
        Private _logRefreshTimer As Timer
        Private _lastLogTimestamp As DateTime = DateTime.MinValue

        ' Manual line-count + offset bookkeeping for the Logs tab.
        ' Replaces the old `_logTextBox.Lines.Length` / `Lines = keep`
        ' approach that was the root cause of the catastrophic UI
        ' lockup: RichTextBox.Lines is an O(text size) accessor that
        ' walks the whole control and allocates a fresh String() array
        ' on every read, and the assignment setter re-parsed the
        ' entire content as RTF. Both were running on every 250ms
        ' tick once the buffer reached the cap, saturating the UI
        ' thread badly enough that even mouse cursor rendering stuttered.
        '
        ' New scheme — never reads .Lines:
        '   _logLineCount: how many lines are currently in the control
        '   _logTotalCharsWritten: monotonic count of all chars ever
        '       appended to the control, including chars since trimmed
        '   _logBaseCharOffset: how many of those chars have been trimmed
        '       away (so TextLength == _logTotalCharsWritten - _logBaseCharOffset)
        '   _logLineEndAbsoluteOffsets: queue of absolute offsets (in
        '       the "ever written" coordinate system) one past each
        '       newline currently visible. On trim we dequeue the
        '       offsets being removed; the last dequeued offset minus
        '       _logBaseCharOffset is the relative cut point inside
        '       the control's current text. Remaining queue entries
        '       stay valid because they're absolute — no per-trim
        '       rewrite of the queue contents needed.
        Private _logLineCount As Integer = 0
        Private _logTotalCharsWritten As Long = 0
        Private _logBaseCharOffset As Long = 0
        Private ReadOnly _logLineEndAbsoluteOffsets As New Queue(Of Long)()

        ' Overview tab controls
        Private _playerCountLabel As Label
        ' Custom owner-drawn list control — see BufferedListView.vb
        ' for the rationale and design notes. Replaces the earlier
        ' native-ListView subclass; the API is no longer ListView-
        ' compatible (use AddColumn / AddRow / ClearRows instead of
        ' Columns.Add / Items.Add / Items.Clear). The 3-second
        ' refresh tick rebuilds the rows via BeginUpdate /
        ' ClearRows / AddRow loop / EndUpdate.
        Private _playerList As BufferedListView

        ' Chat tab controls. _chatList uses the same custom
        ' owner-drawn BufferedListView as the player list and the
        ' file-management lists — plain ListView under WS_EX_COMPOSITED
        ' surfaces per-row paint cascades during window resize, which
        ' BufferedListView avoids with its single-pass OnPaint.
        Private _chatList As BufferedListView
        Private _lastChatTimestamp As DateTime? = Nothing

        ' Config tab controls
        Private _configContent As Panel

        Private _refreshTimer As Timer

        ' Cached latest state so the status line can combine process
        ' state + server state without re-fetching both on every paint.
        Private _latestServerState As ServerStateResponse

        ' Cached latest process state. Drives both the status label
        ' and the Start/Stop/Restart button enabled-state. Updated
        ' every 3s by the refresh timer and immediately after any
        ' button-click operation completes.
        Private _latestProcState As InstanceStatusResponse

        ' Per-instance Show Logs toggle preference, persisted across
        ' panel disposal + reconstruction. InstancePanel is rebuilt
        ' every time the user navigates into an instance node in the
        ' tree (MainForm swaps right-pane contents) and the toggle's
        ' Checked property always starts at False on a fresh panel,
        ' so without this dict the user has to re-flip Show Logs on
        ' every time they revisit an instance. Static so the lifetime
        ' spans all panel instances; manager-restart scope is by
        ' design (a fresh manager session reasonably begins with
        ' logs hidden everywhere). Keyed by instanceId; the value
        ' is just the last-observed Checked state. ConcurrentDictionary
        ' is overkill for UI-thread-only access but cheap and removes
        ' the need to remember the locking discipline.
        Private Shared ReadOnly _showLogsPreferences As _
            New System.Collections.Concurrent.ConcurrentDictionary(Of String, Boolean)

        ' Set during OnLoad's restore-from-pref path to suppress two
        ' side effects of OnToggleShowLogs that are unwanted on
        ' restore: (1) writing the just-applied preference back to
        ' the dict (it's already what we read from), and (2) the
        ' auto-select-Logs-tab inside ShowLogsTab (the user was
        ' presumably on a different tab when they navigated away,
        ' and the previous selection isn't preserved — landing on
        ' Logs unconditionally would override whatever tab the panel
        ' naturally defaults to, which is Overview). Cleared as soon
        ' as the restore assignment returns.
        Private _restoringShowLogs As Boolean = False

        ' Last-selected tab text, persisted across panel disposal +
        ' reconstruction so the user's tab context survives navigation
        ' between instances. Stored by .Text (e.g., "Overview",
        ' "Configuration", "Chat", "Logs", "Saves", "Server Settings")
        ' rather than index because dynamic tabs (Logs toggle, plugin-
        ' supplied managed-files and editor tabs) shift the index
        ' across panels — "Configuration" might be at index 1 on a
        ' Last Oasis instance and index 1 on a Factorio instance too
        ' but the count of trailing tabs varies, so any saved index
        ' would be brittle. Text matching is robust and cross-game-
        ' friendly: shared tab names map across games, game-specific
        ' ones (e.g. "Server Settings" on Factorio) fall through to
        ' the default tab when the saved name doesn't exist on the
        ' new panel.
        '
        ' Static (not per-instance) by design: the user's request is
        ' "remember which tab I was on when comparing instances", not
        ' "remember per instance". One value applies to every
        ' InstancePanel for the lifetime of the manager session.
        Private Shared _lastSelectedTabText As String

        ' Set during OnLoad's tab-restore path to suppress the
        ' SelectedIndexChanged handler's write-back to
        ' _lastSelectedTabText. The Show Logs restore that happens
        ' just before tab restore in OnLoad can itself add the Logs
        ' tab without a selection change (auto-select is gated by
        ' _restoringShowLogs) so the only event we genuinely need
        ' to suppress is the tab-restore's own SelectedTab assignment.
        Private _restoringTabSelection As Boolean = False

        Public Sub New(instanceId As String)
            _instanceId = instanceId
            InitializeControls()
            LoadInstanceData()
            StartRefreshTimer()
        End Sub

        Private Sub StartRefreshTimer()
            _refreshTimer = New Timer()
            _refreshTimer.Interval = 3000
            AddHandler _refreshTimer.Tick, Async Sub(s, e) Await RefreshAllAsync()
            _refreshTimer.Start()
        End Sub

        ''' <summary>
        ''' Restore the Show Logs toggle from its per-instance
        ''' preference. Runs from OnLoad rather than the constructor
        ''' because ShowLogsTab defers its initial buffer fill via
        ''' Me.BeginInvoke, which throws InvalidOperationException
        ''' when the control's handle isn't yet created — and at
        ''' constructor time the panel hasn't been parented yet,
        ''' so the handle doesn't exist.
        '''
        ''' The _restoringShowLogs flag in effect across the
        ''' Checked-set tells OnToggleShowLogs to skip the
        ''' preference write-back (it'd be redundant) and tells
        ''' ShowLogsTab to skip the auto-select-Logs-tab behavior
        ''' (so the user lands on whatever tab the panel defaults
        ''' to, not on Logs).
        ''' </summary>
        Protected Overrides Sub OnLoad(e As EventArgs)
            MyBase.OnLoad(e)

            Dim prefer As Boolean
            If _showLogsPreferences.TryGetValue(_instanceId, prefer) AndAlso prefer Then
                _restoringShowLogs = True
                Try
                    _showLogsToggle.Checked = True
                Finally
                    _restoringShowLogs = False
                End Try
            End If

            ' Restore last-selected tab. Runs AFTER the Show Logs
            ' restore above so a saved "Logs" lookup can succeed
            ' against the just-added Logs tab. If the saved tab
            ' doesn't exist on this instance (e.g., last viewed
            ' "Server Settings" on a Factorio instance, now
            ' navigating to a Last Oasis instance which has no
            ' such tab), the lookup falls through and the panel
            ' lands on its default tab (Overview).
            If Not String.IsNullOrEmpty(_lastSelectedTabText) Then
                Dim target As TabPage = Nothing
                For Each t As TabPage In _tabs.TabPages
                    If String.Equals(t.Text, _lastSelectedTabText, StringComparison.Ordinal) Then
                        target = t
                        Exit For
                    End If
                Next
                If target IsNot Nothing AndAlso target IsNot _tabs.SelectedTab Then
                    _restoringTabSelection = True
                    Try
                        _tabs.SelectedTab = target
                    Finally
                        _restoringTabSelection = False
                    End Try
                End If
            End If
        End Sub

        ''' <summary>
        ''' Tab-change handler that writes the new tab's .Text to
        ''' the class-shared static so the next InstancePanel
        ''' constructed (e.g. when the user navigates to a different
        ''' instance) can restore the same tab. Suppressed during
        ''' the OnLoad restore so the static doesn't get echo-written
        ''' with what we just read. User-initiated tab clicks and
        ''' programmatic auto-selects (e.g. ShowLogsTab auto-select
        ''' on user toggle) both flow through naturally.
        ''' </summary>
        Private Sub OnTabSelectionChanged(sender As Object, e As EventArgs)
            If _restoringTabSelection Then Return
            Dim tab = _tabs.SelectedTab
            If tab IsNot Nothing AndAlso Not String.IsNullOrEmpty(tab.Text) Then
                _lastSelectedTabText = tab.Text
            End If
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _refreshTimer IsNot Nothing Then
                _refreshTimer.Stop()
                _refreshTimer.Dispose()
                _refreshTimer = Nothing
            End If
            If disposing AndAlso _logRefreshTimer IsNot Nothing Then
                _logRefreshTimer.Stop()
                _logRefreshTimer.Dispose()
                _logRefreshTimer = Nothing
            End If
            MyBase.Dispose(disposing)
        End Sub

        Private Sub InitializeControls()
            _nameLabel = New Label()
            _nameLabel.Font = New Font("Segoe UI", 16, FontStyle.Bold)
            _nameLabel.AutoSize = True
            _nameLabel.Location = New Point(20, 15)

            _gameLabel = New Label()
            _gameLabel.Font = New Font("Segoe UI", 10)
            _gameLabel.ForeColor = Color.Gray
            _gameLabel.AutoSize = True
            _gameLabel.Location = New Point(22, 50)

            _statusLabel = New Label()
            _statusLabel.Font = New Font("Segoe UI", 11, FontStyle.Bold)
            _statusLabel.AutoSize = True
            _statusLabel.Location = New Point(22, 75)
            _statusLabel.MaximumSize = New Size(800, 0)

            ' Instance ID backstop — dim, right-click-copyable. X=22
            ' so the header offset loop lands it at the same 2px indent
            ' as the game/status lines.
            _idLabel = PanelIdLabel.Create(New Point(22, 95))
            PanelIdLabel.SetId(_idLabel, _instanceId)

            ' ---- Buttons ----
            Dim buttonY = 115
            _startButton = New Button()
            _startButton.Text = "Start"
            _startButton.Size = New Size(100, 32)
            _startButton.Location = New Point(20, buttonY)
            AddHandler _startButton.Click, Sub(s, e) OnStartInstance()

            _stopButton = New Button()
            _stopButton.Text = "Stop"
            _stopButton.Size = New Size(100, 32)
            _stopButton.Location = New Point(130, buttonY)
            AddHandler _stopButton.Click, Sub(s, e) OnStopInstance()

            _restartButton = New Button()
            _restartButton.Text = "Restart"
            _restartButton.Size = New Size(100, 32)
            _restartButton.Location = New Point(240, buttonY)
            AddHandler _restartButton.Click, Sub(s, e) OnRestartInstance()

            _showLogsToggle = New CheckBox()
            _showLogsToggle.Text = "Show Logs"
            _showLogsToggle.Appearance = Appearance.Button
            _showLogsToggle.TextAlign = ContentAlignment.MiddleCenter
            _showLogsToggle.Size = New Size(100, 32)
            _showLogsToggle.Location = New Point(350, buttonY)
            _showLogsToggle.UseVisualStyleBackColor = True
            ' Toggling adds/removes the Logs tab and starts/stops its
            ' polling timer. Off by default so freshly-opened instance
            ' panels don't pay the log-polling cost until the user
            ' explicitly asks for it.
            AddHandler _showLogsToggle.CheckedChanged, AddressOf OnToggleShowLogs

            _historyButton = New Button()
            _historyButton.Text = "History..."
            _historyButton.Size = New Size(100, 32)
            _historyButton.Location = New Point(460, buttonY)
            AddHandler _historyButton.Click, Sub(s, e) OnOpenHistory()

            ' ---- Header area with buttons, docked to top ----
            ' The tabs below this are Dock.Fill so they always take
            ' whatever remains of the UserControl. No Load handlers
            ' or manual resize math needed — WinForms handles it.
            Dim headerPanel As New Panel()
            headerPanel.Dock = DockStyle.Top
            headerPanel.Height = 155
            headerPanel.Controls.AddRange(New Control() {
                _nameLabel, _gameLabel, _statusLabel, _idLabel,
                _startButton, _stopButton, _restartButton, _showLogsToggle,
                _historyButton
            })

            ' Bottom spacer so the tab control doesn't sit flush against
            ' the bottom edge (which was clipping the last row of content).
            Dim bottomSpacer As New Panel()
            bottomSpacer.Dock = DockStyle.Bottom
            bottomSpacer.Height = 10

            ' Side margins via Padding on the UserControl itself.
            ' We set this AFTER the header's child Locations so the
            ' original (20, 15) coordinates stay right where they were.
            ' Zero the left offset on header children since the form
            ' padding now provides it.
            For Each c As Control In headerPanel.Controls
                c.Location = New Point(c.Location.X - 20, c.Location.Y)
            Next

            ' ---- Tab control fills the rest ----
            _tabs = New TabControl()
            _tabs.Dock = DockStyle.Fill
            _tabs.Font = New Font("Segoe UI", 9.5F)
            ' Multiline so tabs wrap to additional rows when they
            ' don't all fit horizontally, instead of falling back
            ' to the scroll-chevron mode. The scroll-chevron mode
            ' interacts badly with the MainForm WS_EX_COMPOSITED
            ' style: at certain narrow widths there's a width
            ' where, WITHOUT the chevrons the tabs fit, but WITH
            ' the chevrons they don't — so the control ping-pongs
            ' between the two states many times per second,
            ' invalidating the tab strip on each iteration.
            ' WS_EX_COMPOSITED then composites the whole form on
            ' every iteration, which surfaces visually as the
            ' TreeView (and every other descendant) appearing to
            ' refresh continuously until the form is widened past
            ' the oscillation zone. Multiline mode side-steps the
            ' whole thing because there's no scroll-button whose
            ' presence depends on available width — overflow just
            ' wraps to a second tab row. The cost is slightly
            ' less vertical space for tab content on narrow
            ' windows, which is the right trade.
            _tabs.Multiline = True

            _overviewTab = New TabPage("Overview")
            _configTab = New TabPage("Configuration")
            _chatTab = New TabPage("Chat")

            BuildOverviewTab()
            BuildConfigTab()
            BuildChatTab()

            _tabs.TabPages.Add(_overviewTab)
            _tabs.TabPages.Add(_configTab)
            _tabs.TabPages.Add(_chatTab)

            ' Persist tab changes to the class-shared static so the
            ' next InstancePanel constructed can restore the same
            ' tab. Hooked AFTER the three base-tab Add calls above
            ' so the synthetic SelectedIndexChanged that fires on
            ' the first Add (SelectedIndex -1 → 0) doesn't pre-write
            ' "Overview" to the static before any user interaction.
            ' Subsequent Inserts by BuildEditorTabs / BuildManagedFilesTabs
            ' insert before _chatTab (which is at the END), which
            ' keeps Overview at index 0 and doesn't fire the event.
            AddHandler _tabs.SelectedIndexChanged, AddressOf OnTabSelectionChanged

            ' Add Fill child first, then edge-docked children. WinForms
            ' docks in reverse z-order — later-added children claim
            ' their edge before the fill child gets what remains.
            Me.Controls.Add(_tabs)
            Me.Controls.Add(bottomSpacer)
            Me.Controls.Add(headerPanel)

            Me.Padding = New Padding(20, 0, 20, 0)
        End Sub

        ' ---- Tab builders ----

        Private Sub BuildOverviewTab()
            ' Use a Top-docked header bar for the count, Fill-dock the
            ' ListView below. Explicit sizing against tab width breaks
            ' because the tab has Size(0,0) at construction time.
            Dim header As New Panel()
            header.Dock = DockStyle.Top
            header.Height = 30

            _playerCountLabel = New Label()
            _playerCountLabel.Font = New Font("Segoe UI", 10, FontStyle.Bold)
            _playerCountLabel.AutoSize = True
            _playerCountLabel.Location = New Point(10, 8)
            _playerCountLabel.Text = "Players online: 0"
            header.Controls.Add(_playerCountLabel)

            _playerList = New BufferedListView()
            _playerList.Dock = DockStyle.Fill
            ' Two name columns: Character (in-game DisplayName)
            ' and Platform name (PlatformPersona). "Platform name"
            ' rather than "Steam name" because the slot is
            ' game-dependent — Steam handle on LO, Funcom FLS
            ' handle on Conan, multiplayer username on Factorio.
            ' The column reads blank when DisplayName ==
            ' PlatformPersona (typical for Factorio); LO and
            ' Conan render both columns populated because
            ' character names diverge from platform personae
            ' routinely.
            _playerList.AddColumn("Character", 160)
            _playerList.AddColumn("Platform name", 120)
            _playerList.AddColumn("Platform", 70)
            _playerList.AddColumn("Joined", 110)
            _playerList.AddColumn("IP Address", 140)
            _playerList.AddColumn("Platform ID", 140)

            ' Order matters for Dock: Fill child must be added FIRST
            ' (it ends up z-ordered at the back), then Top child.
            _overviewTab.Controls.Add(_playerList)
            _overviewTab.Controls.Add(header)
        End Sub

        Private Sub BuildConfigTab()
            ' Single scrollable panel hosted directly in the TabPage.
            ' Avoid nested scroll panels — the schema form's own
            ' AutoScroll gets in the way otherwise.
            _configContent = New Panel()
            _configContent.Dock = DockStyle.Fill
            _configContent.AutoScroll = True
            _configContent.Padding = New Padding(0)
            _configTab.Controls.Add(_configContent)
        End Sub

        Private Sub BuildChatTab()
            _chatList = New BufferedListView()
            _chatList.Dock = DockStyle.Fill
            ' Chat doesn't use gridlines — the timestamp column's
            ' regular cadence already provides enough visual
            ' separation between rows. Matches the convention of
            ' most chat-log UIs (Discord, IRC clients, etc.).
            _chatList.ShowGridLines = False
            _chatList.AddColumn("Time", 160)
            _chatList.AddColumn("Player", 150)
            _chatList.AddColumn("Message", 500)
            _chatTab.Controls.Add(_chatList)
        End Sub

        ' ---- Data loading ----

        Private Sub LoadInstanceData()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(_instanceId)
                If instanceEntity Is Nothing Then
                    _nameLabel.Text = "Instance not found"
                    Return
                End If

                _nameLabel.Text = instanceEntity.DisplayName
                _gameLabel.Text = $"Game: {instanceEntity.GameId}"

                ' Populate the Configuration tab with the plugin's schema
                ' form. Values come from the instance's stored ConfigJson.
                PopulateConfigTab(instanceEntity)

                ' Phase 4c-4 — structured file editors (e.g.
                ' Factorio's server-settings.json). Built BEFORE
                ' managed-files tabs so editor tabs land closer to
                ' Configuration in the tab order. Both insert at
                ' the current chat-index; editor tabs going first
                ' means managed-dirs find chat shifted by N and
                ' insert after the editors.
                BuildEditorTabs(instanceEntity)

                ' Phase 4c-2 — build the file-management tabs (Saves,
                ' etc.) for plugins that opt in via
                ' IManagedDirectoriesProvider. No-op for plugins that
                ' don't (Last Oasis), so those instances see the same
                ' three-tab layout as before.
                BuildManagedFilesTabs(instanceEntity)
            End Using

            ' Seed with cached state immediately, then kick off refresh
            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr IsNot Nothing Then
                ApplyProcessState(mgr.GetLiveState(_instanceId))
            End If
            Task.Run(Async Function()
                         Await RefreshAllAsync()
                     End Function)
        End Sub

        Private Sub PopulateConfigTab(instanceEntity As InstanceEntity)
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return
                Dim plugin = registry.GetPlugin(instanceEntity.GameId)
                If plugin Is Nothing Then
                    Dim warn As New Label() With {
                        .Text = $"Plugin '{instanceEntity.GameId}' is not loaded.",
                        .AutoSize = True,
                        .Location = New Point(10, 10),
                        .ForeColor = Color.Firebrick
                    }
                    _configContent.Controls.Add(warn)
                    Return
                End If

                ' Parse existing values
                Dim existing As New Dictionary(Of String, String)
                If Not String.IsNullOrEmpty(instanceEntity.ConfigJson) Then
                    Try
                        existing = System.Text.Json.JsonSerializer.Deserialize(
                            Of Dictionary(Of String, String))(instanceEntity.ConfigJson)
                        If existing Is Nothing Then existing = New Dictionary(Of String, String)
                    Catch
                    End Try
                End If

                Dim schema = plugin.GetInstanceConfigSchema()

                ' Filter RCON fields if plugin has no RCON support
                If Not plugin.GetRconProtocol().HasValue Then
                    schema = schema.Where(Function(f) _
                        Not String.Equals(f.Key, "RconPort", StringComparison.OrdinalIgnoreCase) AndAlso
                        Not String.Equals(f.Key, "RconPassword", StringComparison.OrdinalIgnoreCase)
                    ).ToList()
                End If

                ' Append manager-level lifecycle knobs so the read-only
                ' view matches what the edit form shows.
                schema = schema.Concat(CommonConfigFields.GetInstanceLifecycleFields()).ToList()

                Dim result = SchemaFormBuilder.Build(schema, existing)

                ' Hint banner at the top of the scrollable area
                Dim hint As New Label() With {
                    .Text = "(Read-only view — use right-click → Edit Instance... to change)",
                    .AutoSize = True,
                    .Location = New Point(10, 8),
                    .ForeColor = Color.Gray,
                    .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic)
                }
                _configContent.Controls.Add(hint)

                If result.Panel IsNot Nothing Then
                    ' Move every child of the schema form's panel directly
                    ' into _configContent, shifted down by the hint height.
                    ' This avoids nested AutoScroll panels — the root cause
                    ' of the clipping issues we've been fighting.
                    Const HeaderOffset As Integer = 35
                    Dim children = result.Panel.Controls.Cast(Of Control).ToArray()
                    For Each child In children
                        result.Panel.Controls.Remove(child)
                        child.Location = New Point(child.Location.X,
                                                    child.Location.Y + HeaderOffset)
                        _configContent.Controls.Add(child)
                    Next

                    ' Now everything lives directly in _configContent.
                    ' AutoScroll measures all direct children's extents,
                    ' so scrolling will cover the full form naturally.
                    DisableControls(_configContent)
                End If
            Catch ex As Exception
                Dim err As New Label() With {
                    .Text = $"Failed to load configuration: {ex.Message}",
                    .AutoSize = True,
                    .Location = New Point(10, 10),
                    .ForeColor = Color.Firebrick
                }
                _configContent.Controls.Add(err)
            End Try
        End Sub


        Private Sub DisableControls(parent As Control)
            For Each child As Control In parent.Controls
                Dim tb = TryCast(child, TextBox)
                If tb IsNot Nothing Then
                    tb.ReadOnly = True
                    tb.BackColor = SystemColors.Control
                    Continue For
                End If
                Dim cb = TryCast(child, ComboBox)
                If cb IsNot Nothing Then
                    cb.Enabled = False
                    Continue For
                End If
                Dim chk = TryCast(child, CheckBox)
                If chk IsNot Nothing Then
                    chk.AutoCheck = False
                    Continue For
                End If
                Dim nud = TryCast(child, NumericUpDown)
                If nud IsNot Nothing Then
                    nud.ReadOnly = True
                    nud.Increment = 0
                    Continue For
                End If
                If child.HasChildren Then DisableControls(child)
            Next
        End Sub

        ''' <summary>
        ''' Phase 4c-2 — build the file-management tab(s) for plugins
        ''' that opt in via IManagedDirectoriesProvider. Inserts one
        ''' tab per ManagedDirectory between Configuration and Chat
        ''' so the display order is Overview | Configuration |
        ''' [managed dirs…] | Chat. No-op when the plugin doesn't
        ''' implement the interface or returns an empty list — Last
        ''' Oasis takes that branch and gets the same three-tab
        ''' layout it had before this phase.
        '''
        ''' {InstanceId} substitution in RelativePath happens here on
        ''' the manager side per the contract (see ManagedDirectory
        ''' docstring). Plugins return literal "{InstanceId}" tokens
        ''' and never see the substituted form. Today's plugins
        ''' (Factorio) don't use the token because their
        ''' MaxInstancesPerInstallation = 1 means saves are inherently
        ''' install-scoped; the substitution is reserved for future
        ''' multi-instance-per-installation games.
        ''' </summary>
        Private Sub BuildManagedFilesTabs(instanceEntity As InstanceEntity)
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return
                Dim plugin = registry.GetPlugin(instanceEntity.GameId)
                If plugin Is Nothing Then Return

                ' Plugins opt in via IManagedDirectoriesProvider;
                ' bail silently if the plugin doesn't implement it.
                Dim provider = TryCast(plugin, IManagedDirectoriesProvider)
                If provider Is Nothing Then Return

                ' Minimal InstanceConfig — Phase 4c-2 plugins (Factorio)
                ' don't reference CustomFields in GetManagedDirectories.
                ' If a future plugin needs merged install+instance
                ' config here, build it via the same dictionary-merge
                ' logic InstanceManager.StartInstanceAsync uses
                ' (install fields overlaid by instance fields).
                Dim config As New InstanceConfig With {
                    .InstanceId = instanceEntity.InstanceId,
                    .GameId = instanceEntity.GameId,
                    .DisplayName = instanceEntity.DisplayName,
                    .InstallationId = instanceEntity.InstallationId
                }

                Dim dirs = provider.GetManagedDirectories(config)
                If dirs Is Nothing OrElse dirs.Count = 0 Then Return

                ' Tabs go between Configuration and Chat. Find Chat's
                ' index and insert there so the display order is
                ' Overview | Configuration | [managed dirs…] | Chat.
                ' If Chat got removed somehow, fall back to appending.
                Dim insertAt = _tabs.TabPages.IndexOf(_chatTab)
                If insertAt < 0 Then insertAt = _tabs.TabPages.Count

                For Each rawDir In dirs
                    If rawDir Is Nothing Then Continue For
                    Dim resolvedRel = If(rawDir.RelativePath, "").
                        Replace("{InstanceId}", _instanceId)
                    Dim resolved As New ManagedDirectory With {
                        .RelativePath = resolvedRel,
                        .DisplayName = rawDir.DisplayName,
                        .Permissions = rawDir.Permissions,
                        .AllowedExtensions = rawDir.AllowedExtensions
                    }

                    Dim tab As New TabPage(If(rawDir.DisplayName, resolvedRel))
                    Dim panel As New ManagedFilesPanel(_instanceId, resolved) With {
                        .Dock = DockStyle.Fill
                    }
                    tab.Controls.Add(panel)
                    _tabs.TabPages.Insert(insertAt, tab)
                    insertAt += 1
                Next
            Catch ex As Exception
                ' A failure in plugin code or panel construction
                ' shouldn't take down the whole InstancePanel. The
                ' missing tab is its own UI signal that something
                ' went wrong; the message goes to debug output for
                ' a developer running under VS.
                System.Diagnostics.Debug.WriteLine(
                    $"BuildManagedFilesTabs failed: {ex.Message}")
            End Try
        End Sub

        ''' <summary>
        ''' Phase 4c-4 — build structured editor tabs (e.g.
        ''' "Server Settings") for plugins that opt in via
        ''' IInstanceFileEditorProvider. Inserts one tab per
        ''' returned editor between Configuration and the
        ''' managed-files tabs (which haven't been built yet at
        ''' call time, so we insert before Chat and the managed-
        ''' files pass picks up the new chat-index after).
        '''
        ''' Plugin's GetInstanceFileEditors may read fields from
        ''' the merged install+instance ConfigJson — Factorio's
        ''' implementation reads ServerSettings to find the
        ''' correct file path. We mirror the merge logic from
        ''' BuildPreFlightValidationWarnings so the plugin sees
        ''' the same merged view it sees at instance start time.
        '''
        ''' {InstanceId} substitution in RelativePath is the
        ''' Manager's responsibility per the contract. Plugins
        ''' return literal tokens and never see the substituted
        ''' form. None of today's editors use the token, but
        ''' applying the substitution here keeps the contract
        ''' consistent with managed directories.
        ''' </summary>
        Private Sub BuildEditorTabs(instanceEntity As InstanceEntity)
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return
                Dim plugin = registry.GetPlugin(instanceEntity.GameId)
                If plugin Is Nothing Then Return

                Dim provider = TryCast(plugin, IInstanceFileEditorProvider)
                If provider Is Nothing Then Return

                ' Build merged config so plugins reading either
                ' install-level OR instance-level CustomFields see
                ' a consistent dictionary. Same merge order as
                ' InstanceManager.StartInstanceAsync — install
                ' first, instance overlays.
                Dim merged As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                Dim installEntity = TryFindInstall(instanceEntity.InstallationId)
                If installEntity IsNot Nothing AndAlso
                   Not String.IsNullOrEmpty(installEntity.ConfigJson) Then
                    Try
                        Dim installDict = System.Text.Json.JsonSerializer.Deserialize(
                            Of Dictionary(Of String, String))(installEntity.ConfigJson)
                        If installDict IsNot Nothing Then
                            For Each kvp In installDict
                                merged(kvp.Key) = kvp.Value
                            Next
                        End If
                    Catch
                    End Try
                End If
                If Not String.IsNullOrEmpty(instanceEntity.ConfigJson) Then
                    Try
                        Dim instDict = System.Text.Json.JsonSerializer.Deserialize(
                            Of Dictionary(Of String, String))(instanceEntity.ConfigJson)
                        If instDict IsNot Nothing Then
                            For Each kvp In instDict
                                ' Same guard as BuildPreFlightValidationWarnings:
                                ' empty instance values must not clobber
                                ' non-empty install values. Editor tabs that
                                ' surface paths from the merged config
                                ' (Factorio's ServerSettings) would
                                ' otherwise pick the empty override over
                                ' the configured install path.
                                If String.IsNullOrEmpty(kvp.Value) AndAlso
                                   merged.ContainsKey(kvp.Key) AndAlso
                                   Not String.IsNullOrEmpty(merged(kvp.Key)) Then
                                    Continue For
                                End If
                                merged(kvp.Key) = kvp.Value
                            Next
                        End If
                    Catch
                    End Try
                End If

                Dim config As New InstanceConfig With {
                    .InstanceId = instanceEntity.InstanceId,
                    .GameId = instanceEntity.GameId,
                    .DisplayName = instanceEntity.DisplayName,
                    .InstallationId = instanceEntity.InstallationId,
                    .CustomFields = merged
                }

                Dim editors = provider.GetInstanceFileEditors(config)
                If editors Is Nothing OrElse editors.Count = 0 Then Return

                ' Insert before Chat — same anchor as
                ' BuildManagedFilesTabs. Order: Overview |
                ' Configuration | [editor tabs] | [managed dirs
                ' inserted later] | Chat.
                Dim insertAt = _tabs.TabPages.IndexOf(_chatTab)
                If insertAt < 0 Then insertAt = _tabs.TabPages.Count

                For Each rawEditor In editors
                    If rawEditor Is Nothing Then Continue For
                    Dim resolvedRel = If(rawEditor.RelativePath, "").
                        Replace("{InstanceId}", _instanceId)
                    Dim resolved As New InstanceFileEditor With {
                        .Key = rawEditor.Key,
                        .TabTitle = rawEditor.TabTitle,
                        .RelativePath = resolvedRel,
                        .Schema = rawEditor.Schema,
                        .RequiresExistingFile = rawEditor.RequiresExistingFile
                    }

                    Dim tab As New TabPage(If(rawEditor.TabTitle, resolvedRel))
                    Dim panel As New InstanceFileEditorPanel(_instanceId, resolved) With {
                        .Dock = DockStyle.Fill
                    }
                    tab.Controls.Add(panel)
                    _tabs.TabPages.Insert(insertAt, tab)
                    insertAt += 1
                Next
            Catch ex As Exception
                ' Same fail-soft policy as BuildManagedFilesTabs:
                ' a missing editor tab is its own UI signal.
                System.Diagnostics.Debug.WriteLine(
                    $"BuildEditorTabs failed: {ex.Message}")
            End Try
        End Sub

        ''' <summary>
        ''' Helper: open a fresh DB scope and find an installation
        ''' by id. Used by BuildEditorTabs to find the install
        ''' entity for ConfigJson merging without holding the
        ''' caller's scope across the whole tab-build operation.
        ''' </summary>
        Private Function TryFindInstall(installationId As String) As InstallationEntity
            If String.IsNullOrEmpty(installationId) Then Return Nothing
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Return db.Installations.Find(installationId)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ' ---- Refresh ----

        Private Async Function RefreshAllAsync() As Task
            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr Is Nothing Then Return

            Dim procState As InstanceStatusResponse = Nothing
            Dim players As IReadOnlyList(Of PlayerSession) = Nothing
            Dim srvState As ServerStateResponse = Nothing
            Dim chat As IReadOnlyList(Of ChatMessage) = Nothing

            Try
                procState = Await mgr.RefreshInstanceStateAsync(_instanceId)
            Catch
            End Try

            ' Only pull player/server-state/chat data when we think the
            ' instance is actually running. Saves round trips when the
            ' instance is stopped and the node would just return empty.
            Dim isRunning = procState IsNot Nothing AndAlso
                            (procState.CurrentState = GSM.Plugin.InstanceState.Running OrElse
                             procState.CurrentState = GSM.Plugin.InstanceState.Starting)
            If isRunning Then
                Try
                    players = Await mgr.GetPlayersAsync(_instanceId)
                    ' Phase 5g-2d — enrich against the resolver so the
                    ' Character column shows the resolved DisplayName
                    ' (e.g. "site's character") even when this
                    ' /players snapshot hasn't surfaced it yet.
                    players = mgr.EnrichPlayers(_instanceId, players)
                Catch
                End Try
                Try
                    srvState = Await mgr.GetServerStateAsync(_instanceId)
                Catch
                End Try
                Try
                    chat = Await mgr.GetChatHistoryAsync(_instanceId, _lastChatTimestamp, 200)
                Catch
                End Try
            End If

            If Me.IsDisposed Then Return
            If Me.InvokeRequired Then
                Me.BeginInvoke(Sub() ApplyAll(procState, players, srvState, chat, isRunning))
            Else
                ApplyAll(procState, players, srvState, chat, isRunning)
            End If
        End Function

        Private Sub ApplyAll(procState As InstanceStatusResponse,
                              players As IReadOnlyList(Of PlayerSession),
                              srvState As ServerStateResponse,
                              chat As IReadOnlyList(Of ChatMessage),
                              isRunning As Boolean)
            _latestServerState = srvState
            ApplyProcessState(procState)
            If isRunning Then
                ApplyPlayers(players)
                AppendChat(chat, players)
            Else
                ' Clear live data when not running. Chat is part of
                ' "live data" — the Chat tab shows the current
                ' session's chat only; cross-session chat lookups
                ' belong in the History window. When the instance
                ' stops, there's no current session, so the tab
                ' resets to empty and waits for the next start.
                ' _lastChatTimestamp is also reset so a subsequent
                ' start triggers a fresh fetch from the Node rather
                ' than picking up after wherever the last poll left
                ' off (the cursor wouldn't be meaningful across a
                ' stop/start anyway — the new session's players have
                ' new JoinedUtc values).
                _playerList.ClearRows()
                _playerCountLabel.Text = "Players online: 0"
                If _chatList.Rows.Count > 0 Then _chatList.ClearRows()
                _lastChatTimestamp = Nothing
            End If
        End Sub

        Private Sub ApplyProcessState(state As InstanceStatusResponse)
            _latestProcState = state
            If state Is Nothing Then
                _statusLabel.Text = "Unknown"
                _statusLabel.ForeColor = Color.Gray
                RefreshButtonsFromState()
                Return
            End If

            Dim baseText As String
            Dim statusColor As Color

            Select Case state.CurrentState
                Case GSM.Plugin.InstanceState.Running
                    baseText = $"Running (PID {If(state.Pid, 0)})"
                    statusColor = Color.ForestGreen
                Case GSM.Plugin.InstanceState.Starting
                    baseText = "Starting..."
                    statusColor = Color.DarkOrange
                Case GSM.Plugin.InstanceState.Stopping
                    baseText = "Stopping..."
                    statusColor = Color.DarkOrange
                Case GSM.Plugin.InstanceState.Stopped
                    baseText = "Stopped"
                    statusColor = Color.Gray
                Case GSM.Plugin.InstanceState.Crashed
                    baseText = $"Crashed (exit {If(state.LastExitCode, 0)})"
                    statusColor = Color.Firebrick
                Case GSM.Plugin.InstanceState.CrashLoopHalted
                    baseText = "Crash loop halted"
                    statusColor = Color.Firebrick
                Case GSM.Plugin.InstanceState.Updating
                    baseText = "Updating..."
                    statusColor = Color.DarkOrange
                Case GSM.Plugin.InstanceState.WaitingForInput
                    baseText = "Waiting for input"
                    statusColor = Color.DarkOrange
                Case Else
                    baseText = state.CurrentState.ToString()
                    statusColor = Color.Gray
            End Select

            ' Append server-state context when running and we have data.
            If state.CurrentState = GSM.Plugin.InstanceState.Running AndAlso
               _latestServerState IsNot Nothing Then
                Dim extras As New List(Of String)
                If Not String.IsNullOrEmpty(_latestServerState.MatchState) Then
                    extras.Add(_latestServerState.MatchState)
                End If
                If Not String.IsNullOrEmpty(_latestServerState.TileName) Then
                    Dim tileDesc = _latestServerState.TileName
                    If Not String.IsNullOrEmpty(_latestServerState.TileId) Then
                        tileDesc &= $" ({_latestServerState.TileId})"
                    End If
                    extras.Add($"on {tileDesc}")
                ElseIf Not String.IsNullOrEmpty(_latestServerState.TileId) Then
                    extras.Add($"tile {_latestServerState.TileId}")
                End If
                If _latestServerState.BackendRegistered Then
                    extras.Add("backend registered")
                End If
                If extras.Count > 0 Then
                    baseText &= " — " & String.Join(", ", extras)
                End If
            End If

            _statusLabel.Text = baseText
            _statusLabel.ForeColor = statusColor
            RefreshButtonsFromState()
        End Sub

        Private Sub ApplyPlayers(players As IReadOnlyList(Of PlayerSession))
            If players Is Nothing Then players = New List(Of PlayerSession)

            _playerCountLabel.Text = $"Players online: {players.Count}"

            ' Diff-free rebuild is simpler than trying to patch the
            ' ListView in place; player lists are small (<50 typically).
            _playerList.BeginUpdate()
            Try
                _playerList.ClearRows()
                Dim nowUtc = DateTime.UtcNow
                For Each p In players
                    ' Character column: in-game DisplayName, falling
                    ' back to PlatformPersona (LO's case before the
                    ' first Persisting tick lands), then to a literal
                    ' "(unknown)" placeholder. PlatformPersona is the
                    ' right fallback rather than "(unknown)" because
                    ' on a fresh join we know the Steam handle
                    ' immediately and the in-game name only catches
                    ' up a few ticks later; showing the persona
                    ' temporarily reads as "we know who this is,
                    ' character name not resolved yet" rather than
                    ' a scarier "unknown player connected".
                    Dim characterCol = If(Not String.IsNullOrEmpty(p.DisplayName),
                                           p.DisplayName,
                                           If(Not String.IsNullOrEmpty(p.PlatformPersona),
                                              p.PlatformPersona,
                                              "(unknown)"))

                    ' Platform name column: PlatformPersona when it
                    ' actually differs from what we put in Character.
                    ' Equal-or-empty leaves the column blank so
                    ' Factorio (and any session where DisplayName
                    ' fell back to PlatformPersona above) doesn't
                    ' show the same string twice. Ordinal compare is
                    ' deliberate — if a character was renamed to a
                    ' different-case variant of the platform handle,
                    ' showing both is the correct disambiguation.
                    Dim platformCol As String = ""
                    If Not String.IsNullOrEmpty(p.PlatformPersona) AndAlso
                       Not String.Equals(p.PlatformPersona, characterCol, StringComparison.Ordinal) Then
                        platformCol = p.PlatformPersona
                    End If

                    _playerList.AddRow(characterCol,
                                        platformCol,
                                        If(p.Platform, ""),
                                        FormatJoinedAge(nowUtc, p.JoinedUtc),
                                        If(p.RemoteAddress, ""),
                                        If(p.PlatformUserId, ""))
                Next
            Finally
                _playerList.EndUpdate()
            End Try
        End Sub

        Private Shared Function FormatJoinedAge(nowUtc As DateTime, joinedUtc As DateTime) As String
            If joinedUtc = DateTime.MinValue Then Return ""
            Dim span = nowUtc - joinedUtc
            If span.TotalSeconds < 0 Then span = TimeSpan.Zero
            If span.TotalMinutes < 1 Then Return $"{CInt(span.TotalSeconds)}s ago"
            If span.TotalHours < 1 Then Return $"{CInt(span.TotalMinutes)}m ago"
            If span.TotalDays < 1 Then Return $"{CInt(span.TotalHours)}h {span.Minutes}m ago"
            Return $"{CInt(span.TotalDays)}d {span.Hours}h ago"
        End Function

        Private Sub AppendChat(chat As IReadOnlyList(Of ChatMessage),
                                players As IReadOnlyList(Of PlayerSession))
            If chat Is Nothing OrElse chat.Count = 0 Then Return

            ' Advance the cursor based on the FULL received list,
            ' before any filtering. The Node returns chat sorted
            ' ascending by timestamp, so the max here is the last
            ' element — but iterate defensively in case the wire
            ' ordering ever changes. The cursor must advance past
            ' rows that we filter out, otherwise the next poll
            ' would re-fetch them and the loop would never make
            ' progress on a chat-heavy past session whose players
            ' have all disconnected.
            For Each msg In chat
                If msg Is Nothing Then Continue For
                If msg.TimestampUtc > If(_lastChatTimestamp, DateTime.MinValue) Then
                    _lastChatTimestamp = msg.TimestampUtc
                End If
            Next

            ' Filter chat to the current session only. The Node's
            ' chat_messages table is persistent and unfiltered —
            ' it carries every line ever spoken on this instance,
            ' across server restarts and tile changes. The Chat
            ' tab here is a live-session surface, so cross-session
            ' history doesn't belong; that's what the History
            ' window is for. Filter accepts a row iff it matches
            ' a currently-connected player by identity AND its
            ' timestamp is at or after that player's most recent
            ' JoinedUtc. Same filter the Phase 5j purge+rebuild
            ' uses, so InstancePanel and rebuilt History stay
            ' consistent.
            Dim filtered = InstanceManager.FilterChatToCurrentSessions(chat, players)
            If filtered Is Nothing OrElse filtered.Kept Is Nothing OrElse
               filtered.Kept.Count = 0 Then Return

            ' Autoscroll detection: only follow the tail when the
            ' user is already there (or the list is empty). The
            ' "within 3 of the last row" tolerance lets a couple
            ' of new messages slip past while still being followed
            ' if the user is effectively at the bottom; scroll
            ' farther back than that and they take over manual
            ' control. LastVisibleRowIndex returns the index of the
            ' bottom-most visible row, so the >= comparison maps
            ' directly to "the tail is in view".
            Dim shouldAutoscroll = _chatList.Rows.Count = 0 OrElse
                _chatList.LastVisibleRowIndex >= _chatList.Rows.Count - 3

            _chatList.BeginUpdate()
            Try
                For Each msg In filtered.Kept
                    Dim localTime = msg.TimestampUtc.ToLocalTime()
                    ' "yyyy-MM-dd HH:mm:ss" — unambiguous across locales
                    ' and sortable as text. Multi-day chat sessions would
                    ' otherwise show only time, making it impossible to
                    ' tell whether a message was today or last week.
                    _chatList.AddRow(
                        localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        If(msg.DisplayName, ""),
                        If(msg.Text, ""))
                Next

                ' Cap at 500 messages in the view
                Const MaxRows = 500
                While _chatList.Rows.Count > MaxRows
                    _chatList.RemoveRowAt(0)
                End While
            Finally
                _chatList.EndUpdate()
            End Try

            If shouldAutoscroll AndAlso _chatList.Rows.Count > 0 Then
                _chatList.EnsureRowVisible(_chatList.Rows.Count - 1)
            End If
        End Sub

        ' ---- Button handlers ----

        Private Async Sub OnStartInstance()
            ' Pre-flight: ask the plugin to validate the merged
            ' instance+install config, surface any warnings as
            ' warn-and-confirm before kicking off the start. The
            ' canonical example is Factorio's "no save selected"
            ' check — starting without a save just produces an
            ' immediate crash, and the user is much better served
            ' by a clear MessageBox than by digging through the
            ' first 30 lines of factorio-current.log.
            '
            ' Failure modes (DB error, plugin missing, etc.) skip
            ' validation rather than blocking start — we don't want
            ' a transient lookup failure to brick the Start button.
            Try
                Dim warnings = BuildPreFlightValidationWarnings()
                If warnings IsNot Nothing AndAlso warnings.Count > 0 Then
                    Dim msg = "Configuration warnings:" & vbCrLf & vbCrLf &
                              String.Join(vbCrLf & vbCrLf, warnings) & vbCrLf & vbCrLf &
                              "Start anyway?"
                    Dim resp = MessageBox.Show(Me, msg, "Start Instance",
                                                MessageBoxButtons.YesNo,
                                                MessageBoxIcon.Warning)
                    If resp <> DialogResult.Yes Then Return
                End If
            Catch
                ' Validation lookup failed — fall through to the
                ' normal start path. Real config problems will
                ' surface as a crash with the usual diagnostics.
            End Try

            SetButtonsEnabled(False)
            _statusLabel.Text = "Starting..."
            _statusLabel.ForeColor = Color.DarkOrange
            Try
                Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
                If mgr Is Nothing Then
                    MessageBox.Show("InstanceManager not registered.", "Error",
                                  MessageBoxButtons.OK, MessageBoxIcon.Error)
                    Return
                End If
                Dim ok = Await mgr.StartInstanceAsync(_instanceId)
                If Not ok Then
                    MessageBox.Show("Failed to start instance. Check the Manager log for details.",
                                  "Start Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                End If
                Await RefreshAllAsync()
            Catch ex As Exception
                MessageBox.Show($"Failed to start instance:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                              "Start Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Finally
                ' RefreshAllAsync above updated _latestProcState; drive
                ' button enabled-state from that rather than blindly
                ' re-enabling everything. If the refresh threw, state
                ' may be stale — the next 3s tick corrects.
                RefreshButtonsFromState()
            End Try
        End Sub

        ''' <summary>
        ''' Resolve the plugin and merged config, then run
        ''' ValidateConfig against it. Returns the warning list, or
        ''' an empty list when the plugin is unavailable / config
        ''' lookup fails. Routes through InstanceManager.
        ''' GetMergedCustomFields so the validator sees the full
        ''' three-layer merge (shared-config group → installation
        ''' → instance) — the same view StartInstanceAsync hands
        ''' the plugin at launch. An earlier version of this
        ''' method did its own two-layer install+instance merge
        ''' and surfaced spurious "CustomerKey is required"
        ''' warnings for Last Oasis installations whose CustomerKey
        ''' lived on a linked Realm group rather than in install
        ''' ConfigJson; using the canonical merge eliminates that
        ''' class of drift.
        ''' </summary>
        Private Function BuildPreFlightValidationWarnings() As IReadOnlyList(Of String)
            Dim empty As IReadOnlyList(Of String) = New List(Of String)
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return empty
            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr Is Nothing Then Return empty

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(_instanceId)
                If instanceEntity Is Nothing Then Return empty

                Dim plugin = registry.GetPlugin(instanceEntity.GameId)
                If plugin Is Nothing Then Return empty

                ' Single source of truth for merged CustomFields.
                ' GetMergedCustomFields applies the same three-layer
                ' stack StartInstanceAsync uses (Realm group →
                ' installation → instance) with the same
                ' empty-doesn't-clobber-non-empty rule between
                ' layers, so the pre-flight validator sees exactly
                ' the dictionary the plugin will see at launch.
                Dim merged = mgr.GetMergedCustomFields(_instanceId)

                Dim cfg As New InstanceConfig With {
                    .InstanceId = instanceEntity.InstanceId,
                    .GameId = instanceEntity.GameId,
                    .DisplayName = instanceEntity.DisplayName,
                    .InstallationId = instanceEntity.InstallationId,
                    .CustomFields = merged
                }

                Dim result = plugin.ValidateConfig(cfg)
                If result Is Nothing Then Return empty
                Return result
            End Using
        End Function

        Private Async Sub OnStopInstance()
            SetButtonsEnabled(False)
            _statusLabel.Text = "Stopping..."
            _statusLabel.ForeColor = Color.DarkOrange
            Try
                Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
                If mgr Is Nothing Then Return
                Await mgr.StopInstanceAsync(_instanceId)
                Await RefreshAllAsync()
            Catch ex As Exception
                MessageBox.Show($"Failed to stop: {ex.Message}", "Stop Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error)
            Finally
                RefreshButtonsFromState()
            End Try
        End Sub

        Private Async Sub OnRestartInstance()
            SetButtonsEnabled(False)
            _statusLabel.Text = "Restarting..."
            _statusLabel.ForeColor = Color.DarkOrange
            Try
                Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
                If mgr Is Nothing Then Return
                Await mgr.RestartInstanceAsync(_instanceId)
                Await RefreshAllAsync()
            Catch ex As Exception
                MessageBox.Show($"Failed to restart: {ex.Message}", "Restart Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error)
            Finally
                RefreshButtonsFromState()
            End Try
        End Sub

        Private Sub SetButtonsEnabled(enabled As Boolean)
            _startButton.Enabled = enabled
            _stopButton.Enabled = enabled
            _restartButton.Enabled = enabled
        End Sub

        ''' <summary>
        ''' Drive Start/Stop/Restart enabled-state off the latest
        ''' observed instance state. Called from two places:
        '''
        '''   1. End of ApplyProcessState — every 3s refresh tick
        '''   2. Finally of OnStart/Stop/Restart handlers — so the
        '''      buttons un-lock appropriately after an operation
        '''      completes (previously this just re-enabled all 3,
        '''      which is wrong: e.g. after a successful Stop the
        '''      Stop button should be disabled, not re-enabled).
        '''
        ''' Policy:
        '''   Running                    → Stop + Restart enabled
        '''   Stopped/Crashed/Halted     → Start enabled
        '''   Starting/Stopping/Updating → all disabled (transitional;
        '''                                 clicking anything is either
        '''                                 a no-op or risks interrupting
        '''                                 the transition)
        '''   WaitingForInput / unknown  → all disabled (safe default)
        ''' </summary>
        Private Sub RefreshButtonsFromState()
            Dim state = _latestProcState
            If state Is Nothing Then
                SetButtonsEnabled(False)
                Return
            End If

            Select Case state.CurrentState
                Case GSM.Plugin.InstanceState.Running
                    _startButton.Enabled = False
                    _stopButton.Enabled = True
                    _restartButton.Enabled = True
                Case GSM.Plugin.InstanceState.Crashed
                    ' Crashed is a transient state during a node-side
                    ' crash-restart cycle. Stop must remain enabled so
                    ' the user can break out of a crash loop — the node
                    ' checks StopIntentPending after its backoff delay
                    ' and skips the next spawn when set. Start is also
                    ' enabled so a user who already knows what's wrong
                    ' can take over manually rather than waiting for
                    ' the node to halt.
                    _startButton.Enabled = True
                    _stopButton.Enabled = True
                    _restartButton.Enabled = False
                Case GSM.Plugin.InstanceState.Stopped,
                     GSM.Plugin.InstanceState.CrashLoopHalted
                    _startButton.Enabled = True
                    _stopButton.Enabled = False
                    _restartButton.Enabled = False
                Case Else
                    SetButtonsEnabled(False)
            End Select

            ' Phase 5m-2e — if the game plugin isn't loaded, Start and
            ' Restart are never allowed (starting would launch an
            ' unmanageable, untracked process). Stop is left as the
            ' state policy set it above, so a running orphan can still
            ' be stopped. Mirrors the InstanceManager start guard.
            If IsPluginMissing() Then
                _startButton.Enabled = False
                _restartButton.Enabled = False
            End If
        End Sub

        ''' <summary>
        ''' Phase 5m-2e — true when this instance's game plugin isn't
        ''' loaded. On any lookup failure returns False: the
        ''' InstanceManager start guard is the real backstop, so we
        ''' don't risk wrongly locking the buttons on a transient hiccup.
        ''' </summary>
        Private Function IsPluginMissing() As Boolean
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return False
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim ent = db.Instances.Find(_instanceId)
                    If ent Is Nothing Then Return False
                    Return registry.GetPlugin(ent.GameId) Is Nothing
                End Using
            Catch
                Return False
            End Try
        End Function

        ' ============================================================
        '  Logs tab — toggle, build, polling, append
        '
        '  Differences from the older modal LogViewerForm:
        '    - Lives in a tab inside InstancePanel, no separate window.
        '    - Off by default. Toggling _showLogsToggle adds the tab
        '      and starts polling; untoggling removes the tab and
        '      stops polling. Users who never look at logs pay zero
        '      polling cost.
        '    - Smart timestamp prefix detection: lines that already
        '      have a [YYYY...] or HH:MM:SS prefix (UE4, ISO, etc.)
        '      are appended as-is. Lines without get our own full
        '      date+time prefix — just time-of-day isn't enough when
        '      a log might span days.
        '    - Auto-scroll OFF actually preserves position. The old
        '      Clear()-and-rebuild on trim destroyed scroll state;
        '      EM_GETSCROLLPOS / EM_SETSCROLLPOS round-trips it,
        '      with an approximate-line-height adjustment to
        '      compensate when lines roll off the top of the buffer.
        ' ============================================================

        Private Const WM_SETREDRAW As Integer = &HB
        Private Const EM_GETSCROLLPOS As Integer = &H4DD
        Private Const EM_SETSCROLLPOS As Integer = &H4DE

        ' Two SendMessage P/Invokes because the lParam type differs:
        ' WM_SETREDRAW takes IntPtr, EM_*SCROLLPOS takes ByRef Point.
        ' VB.Net DllImport overloading by parameter shape works here.
        <Runtime.InteropServices.DllImport("user32.dll")>
        Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer,
                                              wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        <Runtime.InteropServices.DllImport("user32.dll", EntryPoint:="SendMessageW")>
        Private Shared Function SendMessageScrollPos(hWnd As IntPtr, msg As Integer,
                                                       wParam As IntPtr, ByRef lParam As Point) As IntPtr
        End Function

        Private Const MaxLogBufferedLines As Integer = 5000
        Private Const TrimToLines As Integer = 4000

        ' Detect leading timestamp patterns. Common formats:
        '   UE4:    [2025.10.26-12.34.56:789][...]LogCategory: Message
        '   ISO:    2025-10-26T12:34:56  /  2025-10-26 12:34:56
        '   Time:   [12:34:56] or 12:34:56 (with optional bracket)
        ' Lines matching any of these are appended verbatim — no point
        ' bolting our own timestamp in front of one the source already
        ' provides. Compiled once and reused per line.
        Private Shared ReadOnly s_HasTimestampPrefix As _
            New System.Text.RegularExpressions.Regex(
                "^\[?(\d{4}[\.\-/]\d{2}[\.\-/]\d{2}|\d{2}:\d{2}:\d{2})",
                System.Text.RegularExpressions.RegexOptions.Compiled)

        Private Shared Function HasOwnTimestamp(text As String) As Boolean
            If String.IsNullOrEmpty(text) Then Return False
            Return s_HasTimestampPrefix.IsMatch(text)
        End Function

        Private Sub OnToggleShowLogs(sender As Object, e As EventArgs)
            If _showLogsToggle.Checked Then
                _showLogsToggle.Text = "Hide Logs"
                ShowLogsTab()
            Else
                _showLogsToggle.Text = "Show Logs"
                HideLogsTab()
            End If

            ' Persist the user's intent so navigating away and back
            ' to this instance preserves the toggle state. Skip
            ' during the OnLoad-driven restore: the dict already
            ' holds exactly what we're reading from, no point
            ' writing it back, and writing during restore would
            ' also race with any concurrent constructor running
            ' for the same instanceId (rare but possible if the
            ' user double-clicks the tree node fast enough).
            If Not _restoringShowLogs Then
                _showLogsPreferences(_instanceId) = _showLogsToggle.Checked
            End If
        End Sub

        ''' <summary>
        ''' Public entry point used by MainForm's right-click
        ''' "View Logs" menu item. Enables the Show Logs toggle if
        ''' it isn't already (which builds the tab and starts the
        ''' polling timer via the toggle's CheckedChanged handler
        ''' running synchronously) and brings the tab to the front.
        ''' Idempotent: a redundant call when logs are already
        ''' visible just re-selects the tab.
        ''' </summary>
        Public Sub ActivateLogsTab()
            If Not _showLogsToggle.Checked Then
                ' Setting Checked fires CheckedChanged synchronously,
                ' which routes through OnToggleShowLogs -> ShowLogsTab.
                ' By the time this assignment returns, _logsTab is
                ' non-Nothing and already SelectedTab. Re-selecting
                ' below is therefore redundant in this branch but
                ' harmless — keeping it makes the contract clearer:
                ' after ActivateLogsTab returns, the logs tab is
                ' visible and selected, regardless of prior state.
                _showLogsToggle.Checked = True
            End If
            If _logsTab IsNot Nothing Then
                _tabs.SelectedTab = _logsTab
            End If
        End Sub

        ''' <summary>
        ''' Build the Logs tab UI on demand, hook up the polling
        ''' timer, and seed it with the manager's ring-buffer tail.
        ''' Idempotent — a redundant call is a no-op (defensive
        ''' against double-toggle from rapid clicks).
        ''' </summary>
        Private Sub ShowLogsTab()
            If _logsTab IsNot Nothing Then Return

            _logsTab = New TabPage("Logs")

            Dim toolbar As New Panel() With {
                .Dock = DockStyle.Top,
                .Height = 28
            }
            _logAutoScrollCheckBox = New CheckBox() With {
                .Text = "Auto-scroll",
                .Checked = True,
                .AutoSize = True,
                .Location = New Point(8, 6)
            }
            toolbar.Controls.Add(_logAutoScrollCheckBox)

            _logTextBox = New RichTextBox() With {
                .Dock = DockStyle.Fill,
                .ReadOnly = True,
                .Font = New Font("Consolas", 9.5F),
                .BackColor = Color.FromArgb(30, 30, 30),
                .ForeColor = Color.FromArgb(220, 220, 220),
                .WordWrap = False,
                .MaxLength = Integer.MaxValue,
                .HideSelection = False,
                .DetectUrls = False
            }

            ' Add Fill child first, then Top child — same docking-order
            ' rule the rest of this file follows. Top-docked children
            ' added second claim their edge before the Fill child.
            _logsTab.Controls.Add(_logTextBox)
            _logsTab.Controls.Add(toolbar)

            _tabs.TabPages.Add(_logsTab)
            ' Auto-select on user-initiated toggle so the user
            ' immediately sees what they asked for. Suppressed on
            ' OnLoad-driven restore so the panel lands on its
            ' default tab (Overview) rather than yanking the user
            ' onto Logs every time they navigate back to an
            ' instance that previously had logs visible — they may
            ' have been viewing Configuration or Chat when they
            ' left, and the previous tab selection isn't preserved.
            If Not _restoringShowLogs Then
                _tabs.SelectedTab = _logsTab
            End If

            ' Force handle creation + initial paint. Without this, an
            ' empty Logs tab (ring buffer empty — server stopped, or
            ' freshly added before any logs flow) shows ghost pixels
            ' from whichever tab was visible before. The RichTextBox
            ' doesn't create its window handle until something
            ' touches it (AppendText, .Handle access, etc.); until
            ' then the area where it should paint is left to the
            ' back-buffer. CreateControl forces the handle, Refresh
            ' forces the paint. Once the first line arrives the
            ' AppendLogLinesToTab path would have triggered both as
            ' a side effect, which is why the bug self-corrects on
            ' first content.
            _logTextBox.CreateControl()
            _logTextBox.Refresh()

            _lastLogTimestamp = DateTime.MinValue

            ' Reconnect the log stream if it's idle (e.g. after the
            ' Manager restarted while the instance kept running).
            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr IsNot Nothing Then
                Task.Run(Async Function()
                             Await mgr.EnsureLogStreamAsync(_instanceId)
                         End Function)
            End If

            ' Seed with the manager's ring-buffer tail so the user
            ' sees recent context immediately, not just whatever
            ' arrives after they toggled on.
            '
            ' Deferred via BeginInvoke so OnToggleShowLogs returns
            ' before the (potentially several-hundred-line) initial
            ' fill runs. Without this, the toggle blocks on the
            ' synchronous fill and the tab doesn't paint until
            ' LoadInitialLogs returns — showing up to the user as a
            ' "couple of seconds frozen" UX. With the deferral the
            ' empty tab paints instantly, then content fills in on
            ' the next pump cycle. AppendLineRun's bulk-append keeps
            ' the actual fill cost in the tens of milliseconds.
            Me.BeginInvoke(Sub() LoadInitialLogs())

            ' Kick off a node-side history fetch too — covers the
            ' freshly-restarted-Manager case where the ring buffer
            ' is empty but the node has a longer history.
            Task.Run(Async Function()
                         Await LoadLogsFromNodeAsync()
                     End Function)

            ' 500ms is the same cadence as the (now-removed)
            ' detached LogViewerForm. Per-tick cost is now small
            ' enough that we could go faster, but the rate-limiting
            ' factor is the human reader — noticeable updates at
            ' 500ms, no benefit to going lower. If logs ever feel
            ' sluggish here the right move is switching to a push
            ' subscription on ManagerRingBufferStore rather than
            ' faster polling.
            _logRefreshTimer = New Timer() With {.Interval = 500}
            AddHandler _logRefreshTimer.Tick, AddressOf OnLogsRefreshTick
            _logRefreshTimer.Start()
        End Sub

        ''' <summary>
        ''' Tear down everything ShowLogsTab built. Stop the timer
        ''' first so a tick mid-teardown can't reach a half-disposed
        ''' control. Null the field references so OnLogsRefreshTick
        ''' and the async fetch callbacks (which check _logTextBox
        ''' Is Nothing) bail out instead of touching disposed objects.
        ''' </summary>
        Private Sub HideLogsTab()
            If _logRefreshTimer IsNot Nothing Then
                _logRefreshTimer.Stop()
                _logRefreshTimer.Dispose()
                _logRefreshTimer = Nothing
            End If

            If _logsTab IsNot Nothing Then
                _tabs.TabPages.Remove(_logsTab)
                _logsTab.Dispose()
                _logsTab = Nothing
            End If

            _logTextBox = Nothing
            _logAutoScrollCheckBox = Nothing
            _lastLogTimestamp = DateTime.MinValue

            ' Reset offset bookkeeping so the next ShowLogsTab
            ' starts from a clean coordinate system. Without this,
            ' a second toggle-on would inherit absolute offsets
            ' from the first session and the first trim's relative
            ' cut math would produce a negative number.
            _logLineCount = 0
            _logTotalCharsWritten = 0
            _logBaseCharOffset = 0
            _logLineEndAbsoluteOffsets.Clear()
        End Sub

        Private Sub LoadInitialLogs()
            Dim logStore = ManagerProgram.Services.GetService(Of ManagerRingBufferStore)()
            If logStore Is Nothing Then Return

            Dim lines = logStore.GetTail(_instanceId, 500)
            If lines Is Nothing OrElse lines.Count = 0 Then Return

            AppendLogLinesToTab(lines, 0, lines.Count)
            _lastLogTimestamp = lines(lines.Count - 1).Timestamp
        End Sub

        Private Async Function LoadLogsFromNodeAsync() As Task
            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr Is Nothing Then Return

            Dim lines As IReadOnlyList(Of LogLine) = Nothing
            Try
                lines = Await mgr.GetRecentLogsAsync(_instanceId, 500)
            Catch
                Return
            End Try

            If lines Is Nothing OrElse lines.Count = 0 Then Return
            ' User may have toggled Logs off mid-fetch; bail out before
            ' marshalling to a now-disposed control.
            If Me.IsDisposed OrElse _logTextBox Is Nothing Then Return
            Me.BeginInvoke(Sub() MergeNodeHistory(lines))
        End Function

        Private Sub MergeNodeHistory(lines As IReadOnlyList(Of LogLine))
            ' Re-check after the BeginInvoke marshal — the toggle could
            ' have flipped between Task.Run completion and the UI thread
            ' picking up the callback.
            If _logTextBox Is Nothing Then Return

            Dim startIdx = 0
            For i = lines.Count - 1 To 0 Step -1
                If lines(i).Timestamp <= _lastLogTimestamp Then
                    startIdx = i + 1
                    Exit For
                End If
            Next
            If startIdx >= lines.Count Then Return

            AppendLogLinesToTab(lines, startIdx, lines.Count)
            _lastLogTimestamp = lines(lines.Count - 1).Timestamp
        End Sub

        Private Sub OnLogsRefreshTick(sender As Object, e As EventArgs)
            If _logTextBox Is Nothing Then Return

            Dim logStore = ManagerProgram.Services.GetService(Of ManagerRingBufferStore)()
            If logStore Is Nothing Then Return

            ' Same cursor-by-timestamp pattern as before — pull a
            ' generous tail and find where our cursor lands. Avoids
            ' the "buffer rolls past our index" gap problem.
            Dim lines = logStore.GetTail(_instanceId, 2000)
            If lines Is Nothing OrElse lines.Count = 0 Then Return

            ' Cheap early bail: if the most recent line in the
            ' buffer is at or before our cursor, no new content has
            ' arrived since the last tick. Skip the cursor scan and
            ' the UI work entirely. Idle servers spend most of their
            ' time in this branch.
            If lines(lines.Count - 1).Timestamp <= _lastLogTimestamp Then Return

            Dim startIdx = 0
            For i = lines.Count - 1 To 0 Step -1
                If lines(i).Timestamp <= _lastLogTimestamp Then
                    startIdx = i + 1
                    Exit For
                End If
            Next
            If startIdx >= lines.Count Then Return

            AppendLogLinesToTab(lines, startIdx, lines.Count)
            _lastLogTimestamp = lines(lines.Count - 1).Timestamp
        End Sub

        ''' <summary>
        ''' Append a range of lines to the Logs tab's RichTextBox.
        '''
        ''' Trim is offset-based: when _logLineCount exceeds the cap,
        ''' we dequeue the recorded character offsets for the lines
        ''' being removed and use Select() + SelectedText = "" to
        ''' delete the prefix in place. This avoids the O(N)
        ''' RichTextBox.Lines accessor entirely — the previous
        ''' implementation called .Lines.Length on every tick (just
        ''' the .Length read walks the whole control and allocates
        ''' a String() array) and on cap-hit did `.Lines = keep`
        ''' which forced a full RTF re-parse. Once the buffer
        ''' reached ~4000 lines that was costing 100+ ms per tick
        ''' and saturating the UI thread badly enough to stutter
        ''' the mouse cursor. The new path touches only the prefix
        ''' being removed.
        '''
        ''' Two other things this method does:
        '''  1. Smart timestamp prefix — if the source line already
        '''     has one we don't add another. If it doesn't, we add
        '''     a full date+time so multi-day sessions remain
        '''     disambiguable.
        '''  2. Auto-scroll OFF preserves position. Scroll point is
        '''     snapshotted via EM_GETSCROLLPOS before any mutation
        '''     and restored after, with a per-trimmed-line height
        '''     adjustment so the user's view stays anchored on the
        '''     same content.
        ''' </summary>
        Private Sub AppendLogLinesToTab(lines As IReadOnlyList(Of LogLine),
                                          startIdx As Integer,
                                          endIdx As Integer)
            If startIdx >= endIdx Then Return
            If _logTextBox Is Nothing OrElse _logAutoScrollCheckBox Is Nothing Then Return

            Dim wasAutoScroll = _logAutoScrollCheckBox.Checked
            Dim savedScroll As New Point()
            If Not wasAutoScroll Then
                SendMessageScrollPos(_logTextBox.Handle, EM_GETSCROLLPOS,
                                      IntPtr.Zero, savedScroll)
            End If

            ' Suspend redraw across trim + appends — one paint at the
            ' end is much cheaper than dozens through the loop, and
            ' avoids the user seeing partial/intermediate states.
            SendMessage(_logTextBox.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero)

            ' Briefly clear ReadOnly across the programmatic mutations
            ' below. The rich-edit window responds to EM_REPLACESEL on a
            ' ReadOnly control by calling MessageBeep before performing
            ' the replacement — the append still succeeds, but each call
            ' rings the system bell. AppendText, SelectedText = "",
            ' and the trim's Select+SelectedText all funnel through
            ' EM_REPLACESEL, so any one of them is enough to produce a
            ' continuous ding cascade during a LO startup burst. The
            ' WM_SETREDRAW window above already prevents the user from
            ' seeing or interacting with the control mid-mutation, so
            ' the brief flip is invisible. Restored in the Finally
            ' alongside redraw re-enable.
            _logTextBox.ReadOnly = False

            Dim trimmedLineCount As Integer = 0
            Try
                ' Offset-based trim. _logLineCount is our own counter
                ' incremented per AppendOneLine call below; we never
                ' read _logTextBox.Lines (which is O(text size) and
                ' allocates a fresh String() array every call).
                If _logLineCount > MaxLogBufferedLines Then
                    trimmedLineCount = _logLineCount - TrimToLines
                    Dim cutAbsoluteOffset As Long = 0
                    For i = 1 To trimmedLineCount
                        cutAbsoluteOffset = _logLineEndAbsoluteOffsets.Dequeue()
                    Next
                    Dim relativeCut As Integer = CInt(cutAbsoluteOffset - _logBaseCharOffset)
                    ' Defensive: never select past TextLength. A
                    ' bookkeeping drift (which shouldn't happen, but
                    ' worth not crashing over) would otherwise throw
                    ' inside Select().
                    If relativeCut > _logTextBox.TextLength Then
                        relativeCut = _logTextBox.TextLength
                    End If
                    If relativeCut > 0 Then
                        _logTextBox.Select(0, relativeCut)
                        _logTextBox.SelectedText = ""
                    End If
                    _logBaseCharOffset = cutAbsoluteOffset
                    _logLineCount = TrimToLines
                End If

                ' Group consecutive same-color lines into runs and
                ' append each run as one chunk. Per-line AppendText
                ' was the second-tier bottleneck after we fixed the
                ' .Lines accessor: each line costs ~3 Win32 messages
                ' (selection, color, append), so an initial-load of
                ' 500 lines added up to ~1500 messages serialized on
                ' the UI thread — a couple of perceptible seconds.
                ' Coalescing into runs takes a typical all-normal-
                ' color tail down to a single AppendText call. A
                ' line where IsError flips starts a new run; in
                ' pathological alternating-color logs we'd be back
                ' to per-line cost, but real-world error rates are
                ' well under 1% so the average case is one run.
                Dim runStart = startIdx
                Dim runIsError = lines(startIdx).IsError
                For i = startIdx + 1 To endIdx - 1
                    If lines(i).IsError <> runIsError Then
                        AppendLineRun(lines, runStart, i, runIsError)
                        runStart = i
                        runIsError = lines(i).IsError
                    End If
                Next
                AppendLineRun(lines, runStart, endIdx, runIsError)
            Finally
                _logTextBox.ReadOnly = True
                SendMessage(_logTextBox.Handle, WM_SETREDRAW, New IntPtr(1), IntPtr.Zero)
                _logTextBox.Invalidate()
            End Try

            If wasAutoScroll Then
                _logTextBox.SelectionStart = _logTextBox.TextLength
                _logTextBox.ScrollToCaret()
            Else
                ' Compensate for trimmed lines. Font.Height is an
                ' approximation of line height (true line height is
                ' Font.Height + leading, but the difference is small
                ' enough that the eye doesn't notice a one-pixel-per-
                ' line drift on the rare trim event).
                If trimmedLineCount > 0 Then
                    Dim approxLineHeight = _logTextBox.Font.Height + 1
                    savedScroll.Y -= trimmedLineCount * approxLineHeight
                    If savedScroll.Y < 0 Then savedScroll.Y = 0
                End If
                SendMessageScrollPos(_logTextBox.Handle, EM_SETSCROLLPOS,
                                      IntPtr.Zero, savedScroll)
            End If
        End Sub

        ''' <summary>
        ''' Append a contiguous run of same-color lines to
        ''' _logTextBox as a single chunk. Building the chunk in
        ''' a StringBuilder and calling AppendText once is a large
        ''' win over per-line appends — see the coalescing comment
        ''' in AppendLogLinesToTab for the why. Updates
        ''' _logLineCount, _logTotalCharsWritten, and the newline-
        ''' offset queue atomically once the chunk is committed to
        ''' the control.
        '''
        ''' chunkEndOffsets accumulates the offset (within the
        ''' chunk text) at which each line ends — i.e. one past the
        ''' vbCrLf that terminates that line. Adding the pre-append
        ''' _logTotalCharsWritten value (baseOffset) to each entry
        ''' gives the absolute coordinate-system position of each
        ''' newline, which is what _logLineEndAbsoluteOffsets stores.
        ''' </summary>
        Private Sub AppendLineRun(lines As IReadOnlyList(Of LogLine),
                                    startIdx As Integer,
                                    endIdx As Integer,
                                    isError As Boolean)
            If startIdx >= endIdx Then Return

            Dim sb As New System.Text.StringBuilder()
            Dim chunkEndOffsets As New List(Of Integer)(endIdx - startIdx)
            For i = startIdx To endIdx - 1
                Dim line = lines(i)
                If HasOwnTimestamp(line.Text) Then
                    sb.Append(line.Text)
                Else
                    Dim ts = line.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    sb.Append("["c)
                    sb.Append(ts)
                    sb.Append("] ")
                    sb.Append(line.Text)
                End If
                sb.Append(vbCrLf)
                chunkEndOffsets.Add(sb.Length)
            Next

            Dim chunkText = sb.ToString()
            Dim baseOffset = _logTotalCharsWritten
            Dim runColor = If(isError,
                                Color.FromArgb(255, 100, 100),
                                Color.FromArgb(220, 220, 220))

            _logTextBox.SelectionStart = _logTextBox.TextLength
            _logTextBox.SelectionColor = runColor
            _logTextBox.AppendText(chunkText)

            _logTotalCharsWritten += chunkText.Length
            For Each lineEnd In chunkEndOffsets
                _logLineEndAbsoluteOffsets.Enqueue(baseOffset + lineEnd)
            Next
            _logLineCount += chunkEndOffsets.Count
        End Sub

        ''' <summary>
        ''' Launch the History window pre-filtered to this instance's
        ''' current session identity (tile) and the last hour of
        ''' activity. Falls back to an unfiltered-by-session launch
        ''' if the parser hasn't committed an identity yet (e.g.
        ''' server booting, not yet in-progress).
        ''' </summary>
        Private Sub OnOpenHistory()
            Dim filter As New GSM.Manager.Core.HistoryFilter With {
                .StartUtc = DateTime.UtcNow.AddHours(-1),
                .EndUtc = DateTime.UtcNow,
                .IncludeChat = True,
                .IncludeJoins = True,
                .IncludeLeaves = True
            }

            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr IsNot Nothing Then
                Dim sid = mgr.GetCurrentSessionIdentity(_instanceId)
                If Not String.IsNullOrEmpty(sid) Then
                    filter.SessionIdentity = sid
                End If
            End If

            Dim win As New HistoryWindow(filter)
            win.Show()
        End Sub

    End Class

    ' ============================================================
    '  SchemaFormBuilder — builds a dynamic form from
    '  ConfigFieldDescriptor arrays returned by plugins
    ' ============================================================

    Public Class SchemaFormBuilder

        ' Width that field labels, descriptions, and the Notice banner
        ' wrap to. Fixed (rather than tracking the live container
        ' width) because the form positions controls absolutely by
        ' yOffset — labels are sized once at build. Capping the width
        ' makes long text wrap to multiline within the panel instead
        ' of running off the right edge and forcing a horizontal
        ' scrollbar. Roughly matches the 400px input controls.
        Private Const ContentWidth As Integer = 430

        ''' <summary>
        ''' Builds a Panel containing form controls generated from
        ''' the given config field descriptors. Returns the panel
        ''' and a function that extracts the current field values.
        ''' </summary>
        Public Shared Function Build(schema As IReadOnlyList(Of ConfigFieldDescriptor),
                                     currentValues As Dictionary(Of String, String)
                                     ) As SchemaFormResult
            ' Two-arg form preserved for the read-only Configuration
            ' tabs on InstancePanel / InstallationPanel that don't
            ' have a node connection to enumerate files. ManagedFilePicker
            ' fields render as an empty-dropdown combo — still readable,
            ' the value lives in the .Text just like a TextBox.
            Return Build(schema, currentValues, fileListProvider:=Nothing)
        End Function

        ''' <summary>
        ''' Three-arg form that lets callers supply a file-list
        ''' provider for ManagedFilePicker fields. The provider is
        ''' invoked once per ManagedFilePicker field at render time
        ''' with the descriptor's ManagedDirectoryRef value as input;
        ''' the returned filenames populate that combo's dropdown.
        '''
        ''' The provider call is fire-and-forget on a background
        ''' thread (so a slow node doesn't block form construction)
        ''' and re-marshals back to the UI thread to fill the combo.
        ''' During the in-flight window the combo is fully usable
        ''' for free-text entry; only the dropdown list arrives late.
        ''' </summary>
        Public Shared Function Build(schema As IReadOnlyList(Of ConfigFieldDescriptor),
                                     currentValues As Dictionary(Of String, String),
                                     fileListProvider As Func(Of String, Task(Of IReadOnlyList(Of String)))
                                     ) As SchemaFormResult

            Dim panel As New Panel()
            panel.AutoScroll = True
            Dim controls As New Dictionary(Of String, Control)
            Dim yOffset = 10

            If schema Is Nothing Then
                Return New SchemaFormResult With {
                    .Panel = panel,
                    .ValueExtractor = Function() New Dictionary(Of String, String)
                }
            End If

            For Each field In schema
                ' Notice fields render as a prominent inline banner
                ' instead of a labelled input — a can't-miss callout
                ' for "special criteria" the operator must see. No
                ' control is registered for them, so ValueExtractor
                ' naturally skips them and nothing is persisted.
                If field.FieldType = ConfigFieldType.Notice Then
                    RenderNoticeBanner(panel, field, yOffset)
                    Continue For
                End If

                ' Label — wrap to the content width so a long label
                ' becomes multiline instead of running off the panel.
                Dim lblFont As New Font("Segoe UI", 9, FontStyle.Bold)
                Dim lblText = If(field.Label, field.Key)
                Dim lblSize = TextRenderer.MeasureText(
                    lblText, lblFont,
                    New Size(ContentWidth, Integer.MaxValue),
                    TextFormatFlags.WordBreak Or TextFormatFlags.TextBoxControl)
                Dim lbl As New Label()
                lbl.AutoSize = False
                lbl.Size = New Size(ContentWidth, lblSize.Height + 2)
                lbl.Text = lblText
                lbl.Location = New Point(10, yOffset)
                lbl.Font = lblFont
                panel.Controls.Add(lbl)
                yOffset += lblSize.Height + 4

                ' Description — same width cap so long help text wraps
                ' to multiple lines and the layout advances past the
                ' real (possibly multi-line) height rather than a
                ' fixed single line.
                If Not String.IsNullOrEmpty(field.Description) Then
                    Dim descFont As New Font("Segoe UI", 8)
                    Dim descSize = TextRenderer.MeasureText(
                        field.Description, descFont,
                        New Size(ContentWidth, Integer.MaxValue),
                        TextFormatFlags.WordBreak Or TextFormatFlags.TextBoxControl)
                    Dim descLbl As New Label()
                    descLbl.AutoSize = False
                    descLbl.Size = New Size(ContentWidth, descSize.Height + 2)
                    descLbl.Text = field.Description
                    descLbl.ForeColor = Color.Gray
                    descLbl.Font = descFont
                    descLbl.Location = New Point(10, yOffset)
                    panel.Controls.Add(descLbl)
                    yOffset += descSize.Height + 6
                End If

                ' Input control
                Dim currentValue = ""
                If currentValues IsNot Nothing AndAlso currentValues.ContainsKey(field.Key) Then
                    currentValue = currentValues(field.Key)
                End If
                If String.IsNullOrEmpty(currentValue) Then
                    currentValue = If(field.DefaultValue, "")
                End If

                Dim inputControl As Control = Nothing

                Select Case field.FieldType
                    Case ConfigFieldType.Text, ConfigFieldType.FilePath,
                         ConfigFieldType.FolderPath
                        Dim txt As New TextBox()
                        txt.Text = currentValue
                        txt.Size = New Size(400, 24)
                        txt.Location = New Point(10, yOffset)
                        inputControl = txt

                    Case ConfigFieldType.Password
                        Dim txt As New TextBox()
                        txt.Text = currentValue
                        txt.Size = New Size(400, 24)
                        txt.Location = New Point(10, yOffset)
                        txt.UseSystemPasswordChar = True
                        inputControl = txt

                    Case ConfigFieldType.IntegerField
                        ' Two render paths for IntegerField:
                        '
                        ' 1. DefaultValue is set (e.g. Port="7777"):
                        '    field is required, render as a NumericUpDown
                        '    that enforces Min/Max and provides up/down
                        '    spin buttons. The control physically can't
                        '    be blank, which matches the "required"
                        '    semantics.
                        '
                        ' 2. DefaultValue is empty (e.g. lifecycle
                        '    fields like GracefulTimeoutMs):
                        '    field is optional with a runtime-side
                        '    fallback. Render as a TextBox so blank
                        '    is an expressible state — NumericUpDown
                        '    can't produce a blank value, which is
                        '    why descriptions saying "leave blank for
                        '    the default" used to be lies. The runtime
                        '    consumer (InstanceManager.GetIntField)
                        '    already treats blank/non-numeric/<=0 as
                        '    "use the hardcoded default", so the
                        '    contract works end-to-end.
                        If String.IsNullOrEmpty(field.DefaultValue) Then
                            Dim txt As New TextBox()
                            txt.Text = currentValue
                            txt.Size = New Size(150, 24)
                            txt.Location = New Point(10, yOffset)
                            inputControl = txt
                        Else
                            Dim nud As New NumericUpDown()
                            nud.Minimum = If(field.MinValue, Integer.MinValue)
                            nud.Maximum = If(field.MaxValue, Integer.MaxValue)
                            Dim intVal As Integer = 0
                            Integer.TryParse(currentValue, intVal)
                            nud.Value = Math.Max(nud.Minimum, Math.Min(nud.Maximum, intVal))
                            nud.Size = New Size(150, 24)
                            nud.Location = New Point(10, yOffset)
                            inputControl = nud
                        End If

                    Case ConfigFieldType.BooleanField
                        Dim chk As New CheckBox()
                        chk.Checked = currentValue.Equals("true", StringComparison.OrdinalIgnoreCase)
                        chk.Text = ""
                        chk.Location = New Point(10, yOffset)
                        inputControl = chk

                    Case ConfigFieldType.[Enum]
                        Dim cmb As New ComboBox()
                        cmb.DropDownStyle = ComboBoxStyle.DropDownList
                        If field.EnumValues IsNot Nothing Then
                            For Each enumVal In field.EnumValues
                                cmb.Items.Add(enumVal)
                            Next
                        End If
                        cmb.Text = currentValue
                        cmb.Size = New Size(250, 24)
                        cmb.Location = New Point(10, yOffset)
                        inputControl = cmb

                    Case ConfigFieldType.ManagedFilePicker
                        ' DropDown (not DropDownList) so the user can
                        ' still type a name that doesn't yet appear
                        ' in the listing — covers manual SCP uploads,
                        ' newly-uploaded files, and the case where
                        ' the file list provider isn't available.
                        ' AutoComplete makes the typing path actually
                        ' useful: as the user types, the suggestions
                        ' narrow to matching listed files.
                        Dim cmb As New ComboBox()
                        cmb.DropDownStyle = ComboBoxStyle.DropDown
                        cmb.AutoCompleteMode = AutoCompleteMode.SuggestAppend
                        cmb.AutoCompleteSource = AutoCompleteSource.ListItems
                        cmb.Size = New Size(400, 24)
                        cmb.Location = New Point(10, yOffset)
                        cmb.Text = currentValue

                        ' Lazy-populate. The provider call goes off
                        ' the UI thread (HTTP round trip) and the
                        ' results land back here via BeginInvoke.
                        ' Capture the dirRef and currentValue locally
                        ' so a later iteration of this loop doesn't
                        ' overwrite them in the closure.
                        '
                        ' The actual await machinery lives in a named
                        ' async helper rather than a lambda — VB.Net
                        ' infers Task(Of Object) on Async Function()
                        ' lambdas that don't return a value, which
                        ' triggers "doesn't return value on all paths"
                        ' warnings. The named function with explicit
                        ' `As Task` return type sidesteps that.
                        If fileListProvider IsNot Nothing AndAlso
                           Not String.IsNullOrEmpty(field.ManagedDirectoryRef) Then
                            Dim dirRef = field.ManagedDirectoryRef
                            Dim valueAtRender = currentValue
                            Dim targetCombo = cmb
                            Dim _unused = PopulateManagedFilePickerAsync(
                                targetCombo, fileListProvider, dirRef, valueAtRender)
                        End If
                        inputControl = cmb

                    Case Else
                        Dim txt As New TextBox()
                        txt.Text = currentValue
                        txt.Size = New Size(400, 24)
                        txt.Location = New Point(10, yOffset)
                        inputControl = txt
                End Select

                If inputControl IsNot Nothing Then
                    panel.Controls.Add(inputControl)
                    controls(field.Key) = inputControl
                    yOffset += inputControl.Height + 12
                End If
            Next

            Dim localControls = controls
            Dim localSchema = schema

            Return New SchemaFormResult With {
                .Panel = panel,
                .ValueExtractor = Function()
                                      Dim values As New Dictionary(Of String, String)
                                      For Each field In localSchema
                                          If field.FieldType = ConfigFieldType.Notice OrElse String.IsNullOrEmpty(field.Key) Then Continue For
                                          If localControls.ContainsKey(field.Key) Then
                                              Dim ctrl = localControls(field.Key)
                                              If TypeOf ctrl Is TextBox Then
                                                  values(field.Key) = DirectCast(ctrl, TextBox).Text
                                              ElseIf TypeOf ctrl Is NumericUpDown Then
                                                  values(field.Key) = CInt(DirectCast(ctrl, NumericUpDown).Value).ToString()
                                              ElseIf TypeOf ctrl Is CheckBox Then
                                                  values(field.Key) = DirectCast(ctrl, CheckBox).Checked.ToString().ToLower()
                                              ElseIf TypeOf ctrl Is ComboBox Then
                                                  values(field.Key) = DirectCast(ctrl, ComboBox).Text
                                              End If
                                          End If
                                      Next
                                      Return values
                                  End Function
            }
        End Function

        ''' <summary>
        ''' Render a ConfigFieldType.Notice descriptor as an inline
        ''' amber callout banner: the Label as a bold heading and the
        ''' Description as the body, both wrapped inside a bordered
        ''' panel. Advances yOffset past the banner. No input control
        ''' is created, so the field never contributes to the form's
        ''' extracted values. Both labels use MaximumSize-based
        ''' wrapping (AutoSize grows height) so long bodies don't
        ''' truncate the way a plain description label does.
        ''' </summary>
        Private Shared Sub RenderNoticeBanner(panel As Panel,
                                              field As ConfigFieldDescriptor,
                                              ByRef yOffset As Integer)
            Const innerPad As Integer = 8
            Dim innerWidth = ContentWidth - innerPad * 2

            ' Measure wrapped text explicitly with TextRenderer
            ' rather than trusting Label.AutoSize — AutoSize height is
            ' unreliable before the control is parented / has a handle,
            ' which previously left the panel too short and collapsed
            ' the body text onto the title line. Fixed-size labels
            ' sized from the measurement wrap and stack correctly.
            Dim parts As New List(Of Control)
            Dim curY = innerPad
            Dim measureFlags As TextFormatFlags =
                TextFormatFlags.WordBreak Or TextFormatFlags.TextBoxControl

            If Not String.IsNullOrEmpty(field.Label) Then
                Dim titleFont As New Font("Segoe UI", 9, FontStyle.Bold)
                Dim titleSize = TextRenderer.MeasureText(
                    field.Label, titleFont,
                    New Size(innerWidth, Integer.MaxValue), measureFlags)
                Dim title As New Label()
                title.AutoSize = False
                title.Size = New Size(innerWidth, titleSize.Height + 2)
                title.Font = titleFont
                title.ForeColor = Color.FromArgb(124, 79, 0)
                title.Text = field.Label
                title.Location = New Point(innerPad, curY)
                parts.Add(title)
                curY += titleSize.Height + 6
            End If

            If Not String.IsNullOrEmpty(field.Description) Then
                Dim bodyFont As New Font("Segoe UI", 8.5F)
                Dim bodySize = TextRenderer.MeasureText(
                    field.Description, bodyFont,
                    New Size(innerWidth, Integer.MaxValue), measureFlags)
                Dim body As New Label()
                body.AutoSize = False
                body.Size = New Size(innerWidth, bodySize.Height + 2)
                body.Font = bodyFont
                body.ForeColor = Color.FromArgb(90, 70, 30)
                body.Text = field.Description
                body.Location = New Point(innerPad, curY)
                parts.Add(body)
                curY += bodySize.Height + 2
            End If

            ' Nothing to show (no label, no description) — skip
            ' silently rather than drawing an empty box.
            If parts.Count = 0 Then Return

            Dim banner As New Panel()
            banner.Location = New Point(10, yOffset)
            banner.Size = New Size(ContentWidth, curY + innerPad)
            banner.BorderStyle = BorderStyle.FixedSingle
            banner.BackColor = Color.FromArgb(255, 247, 214)
            For Each c In parts
                banner.Controls.Add(c)
            Next
            panel.Controls.Add(banner)

            yOffset += banner.Height + 12
        End Sub

        ''' <summary>
        ''' Background helper: invokes the file-list provider on the
        ''' calling thread (which the caller has arranged to be the
        ''' UI thread during form construction — Await's first hop
        ''' bounces it off automatically), then re-marshals via
        ''' BeginInvoke to populate the combo's dropdown. Extracted
        ''' from the field-rendering loop so the lambda's Task(Of
        ''' Object) inference quirk doesn't bite — see comment at
        ''' the call site for context.
        '''
        ''' Doesn't touch .Text — the combo's initial text was set
        ''' from currentValue at construction time, and the user may
        ''' have started typing during the in-flight window. Items
        ''' start empty so we just append; no Items.Clear() to
        ''' clobber state. valueAtRender is kept as a parameter for
        ''' a future fallback if the .Text-preservation behaviour
        ''' ever needs to change.
        '''
        ''' All exceptions are swallowed: the user is mid-edit on
        ''' the form when this runs, and a popup over a half-built
        ''' dialog would be jarring. The combo's free-text path
        ''' remains usable on failure, which is functionally
        ''' equivalent to the pre-ManagedFilePicker behaviour.
        ''' </summary>
        Private Shared Async Function PopulateManagedFilePickerAsync(
                targetCombo As ComboBox,
                provider As Func(Of String, Task(Of IReadOnlyList(Of String))),
                dirRef As String,
                valueAtRender As String) As Task
            Try
                Dim files = Await provider.Invoke(dirRef)
                If files Is Nothing Then Return
                If targetCombo.IsDisposed Then Return
                targetCombo.BeginInvoke(
                    Sub()
                        If targetCombo.IsDisposed Then Return
                        For Each fileName In files
                            If Not String.IsNullOrEmpty(fileName) Then
                                targetCombo.Items.Add(fileName)
                            End If
                        Next
                    End Sub)
            Catch
            End Try
        End Function

    End Class

    ''' <summary>
    ''' Result from SchemaFormBuilder.Build — contains the panel
    ''' and a function to extract current values.
    ''' </summary>
    Public Class SchemaFormResult
        Public Property Panel As Panel
        Public Property ValueExtractor As Func(Of Dictionary(Of String, String))
    End Class

End Namespace