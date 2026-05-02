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
        Private _installationsListView As ListView

        Public Sub New(nodeId As String)
            _nodeId = nodeId
            InitializeControls()
            LoadNodeData()
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

            Dim installLabel As New Label()
            installLabel.Text = "Installations"
            installLabel.Font = New Font("Segoe UI", 11, FontStyle.Bold)
            installLabel.AutoSize = True
            installLabel.Location = New Point(0, 110)

            ' Header section docked to top holds the info labels.
            Dim header As New Panel()
            header.Dock = DockStyle.Top
            header.Height = 140
            header.Controls.AddRange(New Control() {
                _nameLabel, _hostLabel, _statusLabel, installLabel
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
                _statusLabel.Text = If(nodeEntity.IsEnabled, "Enabled", "Disabled")
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
        Private _versionLabel As Label
        Private _credentialLabel As Label
        Private _checkUpdatesButton As Button
        Private _updateStatusLabel As Label

        Private _tabs As TabControl
        Private _overviewTab As TabPage
        Private _configTab As TabPage

        Private _instancesList As ListView
        Private _upButton As Button
        Private _downButton As Button
        Private _configContent As Panel

        Public Sub New(installationId As String)
            _installationId = installationId
            InitializeControls()
            LoadInstallationData()
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
            _versionLabel = New Label() With {
                .Font = New Font("Segoe UI", 9),
                .AutoSize = True,
                .Location = New Point(2, 95)
            }
            _credentialLabel = New Label() With {
                .Font = New Font("Segoe UI", 9),
                .AutoSize = True,
                .Location = New Point(2, 115)
            }

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
            header.Height = 150
            header.Controls.AddRange(New Control() {
                _nameLabel, _gameLabel, _pathLabel, _versionLabel, _credentialLabel,
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
                ApplyVersionLabel(inst)

                ' Steam credential label — use reflection so we don't
                ' hard-bind to a specific property name on the entity
                ' (could be AccountName, Username, Login, etc.).
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
                ageSuffix = $", checked {FormatVersionAgo(ago)}"
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

                Dim ok = Await svc.CheckInstallationAsync(
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
                        If ok Then
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
                            _updateStatusLabel.Text = "Check failed (see log for details)"
                            _updateStatusLabel.ForeColor = Color.Firebrick
                        End If
                    End If
                End Using
            Catch ex As Exception
                _updateStatusLabel.Text = $"Check failed: {ex.Message}"
                _updateStatusLabel.ForeColor = Color.Firebrick
            Finally
                _checkUpdatesButton.Enabled = True
            End Try
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

        ' Overview tab controls
        Private _playerCountLabel As Label
        Private _playerList As ListView

        ' Chat tab controls
        Private _chatList As ListView
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
                _nameLabel, _gameLabel, _statusLabel,
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

            _overviewTab = New TabPage("Overview")
            _configTab = New TabPage("Configuration")
            _chatTab = New TabPage("Chat")

            BuildOverviewTab()
            BuildConfigTab()
            BuildChatTab()

            _tabs.TabPages.Add(_overviewTab)
            _tabs.TabPages.Add(_configTab)
            _tabs.TabPages.Add(_chatTab)

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

            _playerList = New ListView()
            _playerList.Dock = DockStyle.Fill
            _playerList.View = View.Details
            _playerList.FullRowSelect = True
            _playerList.GridLines = True
            _playerList.HideSelection = False
            _playerList.Columns.Add("Name", 180)
            _playerList.Columns.Add("Platform", 80)
            _playerList.Columns.Add("Joined", 120)
            _playerList.Columns.Add("IP Address", 150)
            _playerList.Columns.Add("Steam/Platform ID", 140)

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
            _chatList = New ListView()
            _chatList.Dock = DockStyle.Fill
            _chatList.View = View.Details
            _chatList.FullRowSelect = True
            _chatList.GridLines = False
            _chatList.HideSelection = False
            _chatList.Columns.Add("Time", 160)
            _chatList.Columns.Add("Player", 150)
            _chatList.Columns.Add("Message", 500)
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
                AppendChat(chat)
            Else
                ' Clear live data when not running
                _playerList.Items.Clear()
                _playerCountLabel.Text = "Players online: 0"
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
                _playerList.Items.Clear()
                Dim nowUtc = DateTime.UtcNow
                For Each p In players
                    Dim item As New ListViewItem(If(p.Name, "(unknown)"))
                    item.SubItems.Add(If(p.Platform, ""))
                    item.SubItems.Add(FormatJoinedAge(nowUtc, p.JoinedUtc))
                    item.SubItems.Add(If(p.RemoteAddress, ""))
                    item.SubItems.Add(If(p.PlatformUserId, ""))
                    _playerList.Items.Add(item)
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

        Private Sub AppendChat(chat As IReadOnlyList(Of ChatMessage))
            If chat Is Nothing OrElse chat.Count = 0 Then Return

            Dim shouldAutoscroll = _chatList.Items.Count = 0 OrElse
                _chatList.TopItem Is Nothing OrElse
                _chatList.TopItem.Index + _chatList.Items.Count - 1 >= _chatList.Items.Count - 3

            _chatList.BeginUpdate()
            Try
                For Each msg In chat
                    Dim localTime = msg.TimestampUtc.ToLocalTime()
                    ' "yyyy-MM-dd HH:mm:ss" — unambiguous across locales
                    ' and sortable as text. Multi-day chat sessions would
                    ' otherwise show only time, making it impossible to
                    ' tell whether a message was today or last week.
                    Dim item As New ListViewItem(localTime.ToString("yyyy-MM-dd HH:mm:ss"))
                    item.SubItems.Add(If(msg.PlayerName, ""))
                    item.SubItems.Add(If(msg.Text, ""))
                    _chatList.Items.Add(item)
                    If msg.TimestampUtc > If(_lastChatTimestamp, DateTime.MinValue) Then
                        _lastChatTimestamp = msg.TimestampUtc
                    End If
                Next

                ' Cap at 500 messages in the view
                Const MaxRows = 500
                While _chatList.Items.Count > MaxRows
                    _chatList.Items.RemoveAt(0)
                End While
            Finally
                _chatList.EndUpdate()
            End Try

            If shouldAutoscroll AndAlso _chatList.Items.Count > 0 Then
                _chatList.EnsureVisible(_chatList.Items.Count - 1)
            End If
        End Sub

        ' ---- Button handlers ----

        Private Async Sub OnStartInstance()
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
        End Sub

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
            _tabs.SelectedTab = _logsTab

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
            LoadInitialLogs()

            ' Kick off a node-side history fetch too — covers the
            ' freshly-restarted-Manager case where the ring buffer
            ' is empty but the node has a longer history.
            Task.Run(Async Function()
                         Await LoadLogsFromNodeAsync()
                     End Function)

            ' 250ms feels noticeably more live than 500ms without
            ' the trade-offs that bite below ~200ms (selection loss
            ' during copy, UI thread saturation under log floods).
            ' If logs ever feel sluggish at this interval the right
            ' move isn't a faster timer — it's switching to a
            ' subscription on ManagerRingBufferStore.LineSubscription
            ' so updates push instead of poll.
            _logRefreshTimer = New Timer() With {.Interval = 250}
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

            ' Same cursor-by-timestamp pattern as LogViewerForm — pull
            ' a generous tail and find where our cursor lands. Avoids
            ' the "buffer rolls past our index" gap problem.
            Dim lines = logStore.GetTail(_instanceId, 2000)
            If lines Is Nothing OrElse lines.Count = 0 Then Return

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
        ''' Three things this does that the old LogViewerForm did not:
        '''  1. Smart timestamp prefix — if the source line already has
        '''     one we don't add another. If it doesn't, we add a full
        '''     date+time so multi-day sessions remain disambiguable.
        '''  2. Auto-scroll OFF actually preserves position. We snapshot
        '''     the scroll point with EM_GETSCROLLPOS before any
        '''     mutation and restore it after.
        '''  3. Trim compensation — when the buffer hits the cap and we
        '''     remove old lines from the top, the saved scroll Y is
        '''     adjusted by approximately the height of the removed
        '''     lines so the user's view stays anchored on the same
        '''     content. Approximate (font height ignores line spacing
        '''     deltas) but visibly correct in practice.
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
            Dim trimmedLineCount As Integer = 0
            Try
                If _logTextBox.Lines.Length > MaxLogBufferedLines Then
                    Dim allLines = _logTextBox.Lines
                    trimmedLineCount = allLines.Length - TrimToLines
                    Dim keep = allLines.Skip(trimmedLineCount).ToArray()
                    _logTextBox.Clear()
                    _logTextBox.Lines = keep
                End If

                For i = startIdx To endIdx - 1
                    Dim line = lines(i)
                    Dim textToAppend As String
                    If HasOwnTimestamp(line.Text) Then
                        textToAppend = line.Text & vbCrLf
                    Else
                        Dim ts = line.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        textToAppend = $"[{ts}] {line.Text}{vbCrLf}"
                    End If
                    Dim lineColor = If(line.IsError,
                                        Color.FromArgb(255, 100, 100),
                                        Color.FromArgb(220, 220, 220))
                    _logTextBox.SelectionStart = _logTextBox.TextLength
                    _logTextBox.SelectionColor = lineColor
                    _logTextBox.AppendText(textToAppend)
                Next
            Finally
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
    '  LogViewerForm — separate window showing live log output
    ' ============================================================

    Public Class LogViewerForm
        Inherits Form

        Private ReadOnly _instanceId As String
        Private _logTextBox As RichTextBox
        Private _autoScrollCheckBox As CheckBox
        Private _refreshTimer As Timer
        Private _lastSeenTimestamp As DateTime = DateTime.MinValue

        Public Sub New(instanceId As String)
            FormIconHelper.ApplyTo(Me)
            _instanceId = instanceId
            InitializeControls()

            ' Reconnect the log stream if it's not active (e.g. after
            ' the Manager restarted while the instance kept running).
            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr IsNot Nothing Then
                Task.Run(Async Function()
                             Await mgr.EnsureLogStreamAsync(instanceId)
                         End Function)
            End If

            LoadRecentLogs()
            StartRefreshTimer()

            ' Kick off a history fetch from the node so even a freshly
            ' restarted Manager shows context immediately.
            Task.Run(Async Function()
                         Await LoadHistoryFromNodeAsync()
                     End Function)
        End Sub

        Private Async Function LoadHistoryFromNodeAsync() As Task
            Dim mgr = ManagerProgram.Services.GetService(Of InstanceManager)()
            If mgr Is Nothing Then Return

            Dim lines As IReadOnlyList(Of LogLine) = Nothing
            Try
                lines = Await mgr.GetRecentLogsAsync(_instanceId, 500)
            Catch
                Return
            End Try

            If lines Is Nothing OrElse lines.Count = 0 Then Return

            ' Only display lines newer than what we already have. If the
            ' Manager buffer was empty on open, _lastSeenTimestamp is
            ' DateTime.MinValue and everything qualifies.
            If Me.IsDisposed Then Return
            Me.BeginInvoke(Sub() MergeHistory(lines))
        End Function

        Private Sub MergeHistory(lines As IReadOnlyList(Of LogLine))
            Dim startIdx = 0
            For i = lines.Count - 1 To 0 Step -1
                If lines(i).Timestamp <= _lastSeenTimestamp Then
                    startIdx = i + 1
                    Exit For
                End If
            Next
            If startIdx >= lines.Count Then Return

            AppendLogLinesBatch(lines, startIdx, lines.Count)
            _lastSeenTimestamp = lines(lines.Count - 1).Timestamp
        End Sub

        Private Sub StartRefreshTimer()
            _refreshTimer = New Timer()
            _refreshTimer.Interval = 500
            AddHandler _refreshTimer.Tick, AddressOf OnRefreshTick
            _refreshTimer.Start()
        End Sub

        Private Const WM_SETREDRAW As Integer = &HB

        <Runtime.InteropServices.DllImport("user32.dll")>
        Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        Private Sub OnRefreshTick(sender As Object, e As EventArgs)
            Dim logStore = ManagerProgram.Services.GetService(Of ManagerRingBufferStore)()
            If logStore Is Nothing Then Return

            ' Pull a generous tail and find where our cursor falls.
            ' Using timestamp as cursor avoids the "count stalls at 1000"
            ' problem when the ring buffer tail is full.
            Dim lines = logStore.GetTail(_instanceId, 2000)
            If lines Is Nothing OrElse lines.Count = 0 Then Return

            ' Find the first index whose timestamp is strictly greater
            ' than what we've already rendered. If the buffer has rolled
            ' far enough that our cursor is off the front, we start from
            ' index 0 — which may duplicate a line if timestamps are equal,
            ' but is the safest way to avoid gaps.
            Dim startIdx = 0
            For i = lines.Count - 1 To 0 Step -1
                If lines(i).Timestamp <= _lastSeenTimestamp Then
                    startIdx = i + 1
                    Exit For
                End If
            Next

            If startIdx >= lines.Count Then Return

            AppendLogLinesBatch(lines, startIdx, lines.Count)
            _lastSeenTimestamp = lines(lines.Count - 1).Timestamp
        End Sub

        Private Const MaxBufferedLines As Integer = 5000
        Private Const TrimToLines As Integer = 4000

        ''' <summary>
        ''' Appends a range of lines in one batch. Suspends RichTextBox
        ''' redraws during the append so the UI doesn't thrash through
        ''' hundreds of scroll/paint cycles.
        ''' </summary>
        Private Sub AppendLogLinesBatch(lines As IReadOnlyList(Of LogLine), startIdx As Integer, endIdx As Integer)
            If startIdx >= endIdx Then Return

            ' Suspend redraw while we batch-append
            SendMessage(_logTextBox.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero)
            Try
                ' Trim oldest lines first if we're above the cap, so we
                ' don't let the control grow unbounded. Keep the newest
                ' TrimToLines so there's still useful history visible.
                If _logTextBox.Lines.Length > MaxBufferedLines Then
                    Dim allLines = _logTextBox.Lines
                    Dim keep = allLines.Skip(allLines.Length - TrimToLines).ToArray()
                    _logTextBox.Clear()
                    _logTextBox.Lines = keep
                End If

                For i = startIdx To endIdx - 1
                    Dim line = lines(i)
                    Dim timestamp = line.Timestamp.ToString("HH:mm:ss.fff")
                    Dim lineColor = If(line.IsError, Color.FromArgb(255, 100, 100), Color.FromArgb(220, 220, 220))

                    _logTextBox.SelectionStart = _logTextBox.TextLength
                    _logTextBox.SelectionColor = Color.Gray
                    _logTextBox.AppendText($"[{timestamp}] ")
                    _logTextBox.SelectionColor = lineColor
                    _logTextBox.AppendText(line.Text & vbCrLf)
                Next
            Finally
                ' Resume redraw and invalidate so one paint covers everything
                SendMessage(_logTextBox.Handle, WM_SETREDRAW, New IntPtr(1), IntPtr.Zero)
                _logTextBox.Invalidate()
            End Try

            If _autoScrollCheckBox.Checked Then
                _logTextBox.SelectionStart = _logTextBox.TextLength
                _logTextBox.ScrollToCaret()
            End If
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _refreshTimer IsNot Nothing Then
                _refreshTimer.Stop()
                _refreshTimer.Dispose()
                _refreshTimer = Nothing
            End If
            MyBase.Dispose(disposing)
        End Sub

        Private Sub InitializeControls()
            Me.Text = $"Logs — {_instanceId}"
            Me.Size = New Size(900, 600)
            Me.StartPosition = FormStartPosition.CenterParent

            _autoScrollCheckBox = New CheckBox()
            _autoScrollCheckBox.Text = "Auto-scroll"
            _autoScrollCheckBox.Checked = True
            _autoScrollCheckBox.Dock = DockStyle.Top
            _autoScrollCheckBox.Padding = New Padding(5)

            _logTextBox = New RichTextBox()
            _logTextBox.Dock = DockStyle.Fill
            _logTextBox.ReadOnly = True
            _logTextBox.Font = New Font("Consolas", 9.5F)
            _logTextBox.BackColor = Color.FromArgb(30, 30, 30)
            _logTextBox.ForeColor = Color.FromArgb(220, 220, 220)
            _logTextBox.WordWrap = False
            _logTextBox.MaxLength = Integer.MaxValue

            Me.Controls.Add(_logTextBox)
            Me.Controls.Add(_autoScrollCheckBox)
        End Sub

        Private Sub LoadRecentLogs()
            Dim logStore = ManagerProgram.Services.GetService(Of ManagerRingBufferStore)()
            If logStore Is Nothing Then Return

            Dim lines = logStore.GetTail(_instanceId, 500)
            If lines Is Nothing OrElse lines.Count = 0 Then Return

            AppendLogLinesBatch(lines, 0, lines.Count)
            _lastSeenTimestamp = lines(lines.Count - 1).Timestamp
        End Sub

        ''' <summary>
        ''' Appends a log line to the display. Can be called from
        ''' any thread — marshals to UI thread automatically.
        ''' </summary>
        Public Sub AppendLogLine(line As LogLine)
            If Me.InvokeRequired Then
                Me.BeginInvoke(Sub() AppendLogLine(line))
                Return
            End If

            Dim timestamp = line.Timestamp.ToString("HH:mm:ss.fff")
            Dim prefix = $"[{timestamp}] "
            Dim lineColor = If(line.IsError, Color.FromArgb(255, 100, 100), Color.FromArgb(220, 220, 220))

            _logTextBox.SelectionStart = _logTextBox.TextLength
            _logTextBox.SelectionColor = Color.Gray
            _logTextBox.AppendText(prefix)
            _logTextBox.SelectionColor = lineColor
            _logTextBox.AppendText(line.Text & vbCrLf)

            If _autoScrollCheckBox.Checked Then
                _logTextBox.SelectionStart = _logTextBox.TextLength
                _logTextBox.ScrollToCaret()
            End If
        End Sub

    End Class

    ' ============================================================
    '  SchemaFormBuilder — builds a dynamic form from
    '  ConfigFieldDescriptor arrays returned by plugins
    ' ============================================================

    Public Class SchemaFormBuilder

        ''' <summary>
        ''' Builds a Panel containing form controls generated from
        ''' the given config field descriptors. Returns the panel
        ''' and a function that extracts the current field values.
        ''' </summary>
        Public Shared Function Build(schema As IReadOnlyList(Of ConfigFieldDescriptor),
                                     currentValues As Dictionary(Of String, String)
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
                ' Label
                Dim lbl As New Label()
                lbl.Text = If(field.Label, field.Key)
                lbl.AutoSize = True
                lbl.Location = New Point(10, yOffset)
                lbl.Font = New Font("Segoe UI", 9, FontStyle.Bold)
                panel.Controls.Add(lbl)
                yOffset += 20

                ' Description
                If Not String.IsNullOrEmpty(field.Description) Then
                    Dim descLbl As New Label()
                    descLbl.Text = field.Description
                    descLbl.AutoSize = True
                    descLbl.ForeColor = Color.Gray
                    descLbl.Font = New Font("Segoe UI", 8)
                    descLbl.Location = New Point(10, yOffset)
                    panel.Controls.Add(descLbl)
                    yOffset += 18
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