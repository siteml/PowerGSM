Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Text.Json
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
'  NewInstallationForm — create a new game server installation
'
'  Steps:
'    1. Select a node (from database)
'    2. Select a game (from loaded plugins)
'    3. Choose install method
'    4. Fill in plugin-specific config fields (dynamic form)
'    5. Set install path and display name
'    6. Optionally create the first instance
'
'  Default-value behaviour worth documenting:
'
'    Display Name — left blank by default; on save, falls back
'      to the plugin's DisplayName if blank. Pre-filling
'      "Factorio" when the user picks Factorio (the old
'      behaviour) didn't survive a game change cleanly and
'      gave users a value they almost always wanted to replace
'      anyway.
'
'    Install Path — fetched asynchronously from the selected
'      node's status (NodeStatusResponse.ServersDirectory),
'      then suggested as "{ServersDir}/{gameId}". Falls back
'      to a generic placeholder if the node can't be reached.
'      Updated automatically when node or game selection
'      changes UNTIL the user manually edits the field, after
'      which we leave it alone (their edit is authoritative).
'
'    Ports — when "Create first instance" is checked and the
'      plugin declares port-typed fields (IsPort=True), we run
'      PortAllocator at save time and inject the suggested
'      values into the new instance's ConfigJson. The user
'      can edit these later via Edit Instance.
' ============================================================

Namespace GSM.Manager.UI

    Public Class NewInstallationForm
        Inherits Form

        Private _nodeComboBox As ComboBox
        Private _gameComboBox As ComboBox
        Private _methodComboBox As ComboBox
        Private _nameTextBox As TextBox
        Private _pathTextBox As TextBox
        Private _createInstanceCheckBox As CheckBox
        Private _instanceNameTextBox As TextBox
        Private _configPanel As Panel
        Private _saveButton As Button
        Private _cancelButton As Button
        Private _steamCredComboBox As ComboBox
        Private _runRedistCheckBox As CheckBox

        Private _schemaResult As SchemaFormResult
        Private _nodeEntities As List(Of NodeEntity)
        Private _steamCredIds As New List(Of String)

        ' Notice panel — rendered between the config panel and the
        ' action buttons, populated from
        ' IInstallationNoticeProvider.GetPreInstallNotices on every
        ' game-selection change. Has variable height (sized to its
        ' content) and is hidden entirely when the selected plugin
        ' has no notices to show. The buttons below it are
        ' repositioned by RebuildNoticesPanel so the layout stays
        ' coherent across plugin switches.
        Private _noticesPanel As Panel
        Private _noticesBaseY As Integer

        ' Optional pre-selected node ID, captured in the constructor
        ' for the right-click "Add Installation..." path. Selected
        ' after LoadNodes runs so the combo's selection actually
        ' reflects it.
        Private ReadOnly _preselectedNodeId As String

        ' Path-suggestion bookkeeping. _pathUserEdited becomes True
        ' the moment the user types into _pathTextBox; once true, we
        ' stop overwriting their edits via the auto-suggest. The
        ' suppress flag wraps programmatic writes so they don't
        ' trigger the "user edited" detection.
        Private _pathUserEdited As Boolean = False
        Private _suppressPathChange As Boolean = False

        ' Cache of the most recently selected node's status. Lets us
        ' build the suggested install path without re-fetching every
        ' time the game changes. Cleared and re-fetched on node
        ' selection change.
        Private _cachedNodeStatus As NodeStatusResponse

        ' Cancellation source for the in-flight node-status fetch.
        ' If the user changes node selection mid-fetch, we abandon
        ' the older request rather than letting two responses race.
        Private _statusFetchCts As CancellationTokenSource

        ''' <summary>
        ''' Construct the form, optionally with a pre-selected node.
        ''' The right-click "Add Installation..." flow on the tree
        ''' supplies the right-clicked node's ID; the Tools / Nodes
        ''' menu path passes Nothing and lets the user pick from
        ''' the dropdown.
        ''' </summary>
        Public Sub New(Optional preselectedNodeId As String = Nothing)
            FormIconHelper.ApplyTo(Me)
            _preselectedNodeId = preselectedNodeId
            InitializeControls()
            LoadNodes()
            LoadGames()
            ' Initial path suggestion — fire and forget. Both the node
            ' and game combos have valid selections at this point
            ' (LoadNodes / LoadGames pick index 0), so this triggers
            ' a fetch and populates the path field with whatever the
            ' node reports.
            Task.Run(Async Function()
                         Await RefreshSuggestedInstallPathAsync()
                     End Function)
        End Sub

        Private Sub InitializeControls()
            Me.Text = "New Installation"
            Me.Size = New Size(600, 740)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.AutoScroll = True

            Dim y = 20

            ' Node
            AddLabel("Node:", 20, y)
            _nodeComboBox = New ComboBox()
            _nodeComboBox.Location = New Point(150, y)
            _nodeComboBox.Size = New Size(400, 24)
            _nodeComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            ' Re-suggest the install path when the node changes —
            ' different nodes have different ServersDirectory values.
            AddHandler _nodeComboBox.SelectedIndexChanged, AddressOf OnNodeChanged
            Me.Controls.Add(_nodeComboBox)
            y += 35

            ' Game
            AddLabel("Game:", 20, y)
            _gameComboBox = New ComboBox()
            _gameComboBox.Location = New Point(150, y)
            _gameComboBox.Size = New Size(400, 24)
            _gameComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            AddHandler _gameComboBox.SelectedIndexChanged, AddressOf OnGameChanged
            Me.Controls.Add(_gameComboBox)
            y += 35

            ' Install method
            AddLabel("Install Method:", 20, y)
            _methodComboBox = New ComboBox()
            _methodComboBox.Location = New Point(150, y)
            _methodComboBox.Size = New Size(200, 24)
            _methodComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            Me.Controls.Add(_methodComboBox)
            y += 35

            ' Display name — left blank by default. PlaceholderText
            ' surfaces the "uses game name if blank" affordance
            ' inline; OnSave applies the fallback. Pre-filling the
            ' game's DisplayName here was confusing across game
            ' changes and most users replaced it anyway.
            AddLabel("Display Name:", 20, y)
            _nameTextBox = New TextBox()
            _nameTextBox.Location = New Point(150, y)
            _nameTextBox.Size = New Size(400, 24)
            _nameTextBox.PlaceholderText = "(uses game name if blank)"
            Me.Controls.Add(_nameTextBox)
            y += 35

            ' Install path — populated asynchronously from the node's
            ' ServersDirectory + selected game's GameId. User edits
            ' lock in via _pathUserEdited so we don't stomp on them.
            AddLabel("Install Path:", 20, y)
            _pathTextBox = New TextBox()
            _pathTextBox.Location = New Point(150, y)
            _pathTextBox.Size = New Size(400, 24)
            _pathTextBox.PlaceholderText = "(suggested from node configuration)"
            AddHandler _pathTextBox.TextChanged, AddressOf OnPathTextChanged
            Me.Controls.Add(_pathTextBox)
            y += 35

            ' Steam credentials
            AddLabel("Steam Account:", 20, y)
            _steamCredComboBox = New ComboBox()
            _steamCredComboBox.Location = New Point(150, y)
            _steamCredComboBox.Size = New Size(300, 24)
            _steamCredComboBox.DropDownStyle = ComboBoxStyle.DropDownList
            Me.Controls.Add(_steamCredComboBox)
            LoadSteamCredentials()
            y += 35

            ' Run _CommonRedist toggle — off by default since most
            ' machines already have the redistributables, and without
            ' an elevated node each redist triggers a UAC prompt.
            _runRedistCheckBox = New CheckBox()
            _runRedistCheckBox.Text = "Run _CommonRedist installers after install (requires elevated node)"
            _runRedistCheckBox.Checked = False
            _runRedistCheckBox.AutoSize = True
            _runRedistCheckBox.Location = New Point(20, y)
            Me.Controls.Add(_runRedistCheckBox)
            y += 30

            ' Create first instance
            _createInstanceCheckBox = New CheckBox()
            _createInstanceCheckBox.Text = "Create first instance"
            _createInstanceCheckBox.Checked = True
            _createInstanceCheckBox.Location = New Point(20, y)
            _createInstanceCheckBox.AutoSize = True
            AddHandler _createInstanceCheckBox.CheckedChanged,
                Sub(s, e) _instanceNameTextBox.Enabled = _createInstanceCheckBox.Checked
            Me.Controls.Add(_createInstanceCheckBox)
            y += 30

            AddLabel("Instance Name:", 20, y)
            _instanceNameTextBox = New TextBox()
            _instanceNameTextBox.Location = New Point(150, y)
            _instanceNameTextBox.Size = New Size(400, 24)
            _instanceNameTextBox.Text = "Server 1"
            Me.Controls.Add(_instanceNameTextBox)
            y += 40

            ' Plugin config panel
            Dim configLabel As New Label()
            configLabel.Text = "Game Configuration"
            configLabel.Font = New Font("Segoe UI", 10, FontStyle.Bold)
            configLabel.AutoSize = True
            configLabel.Location = New Point(20, y)
            Me.Controls.Add(configLabel)
            y += 25

            _configPanel = New Panel()
            _configPanel.Location = New Point(20, y)
            _configPanel.Size = New Size(540, 250)
            _configPanel.BorderStyle = BorderStyle.FixedSingle
            _configPanel.AutoScroll = True
            Me.Controls.Add(_configPanel)
            y += 260

            ' Notices container — starts empty (hidden). Width matches
            ' the config panel; height is computed by
            ' RebuildNoticesPanel from notice content. The base Y is
            ' captured here so RebuildNoticesPanel can reposition the
            ' buttons relative to it without re-deriving the layout.
            _noticesBaseY = y
            _noticesPanel = New Panel()
            _noticesPanel.Location = New Point(20, y)
            _noticesPanel.Size = New Size(540, 0)
            _noticesPanel.Visible = False
            Me.Controls.Add(_noticesPanel)

            ' Buttons
            _saveButton = New Button()
            _saveButton.Text = "Create"
            _saveButton.Size = New Size(100, 32)
            _saveButton.Location = New Point(350, y)
            AddHandler _saveButton.Click, AddressOf OnSave
            Me.Controls.Add(_saveButton)

            _cancelButton = New Button()
            _cancelButton.Text = "Cancel"
            _cancelButton.Size = New Size(100, 32)
            _cancelButton.Location = New Point(460, y)
            _cancelButton.DialogResult = DialogResult.Cancel
            Me.Controls.Add(_cancelButton)

            Me.AcceptButton = _saveButton
            Me.CancelButton = _cancelButton
        End Sub

        Private Sub LoadNodes()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                _nodeEntities = db.Nodes.Where(Function(n) n.IsEnabled).ToList()
                _nodeComboBox.Items.Clear()
                For Each nodeEnt In _nodeEntities
                    _nodeComboBox.Items.Add($"{nodeEnt.DisplayName} ({nodeEnt.HostAddress}:{nodeEnt.Port})")
                Next
                If _nodeComboBox.Items.Count > 0 Then
                    ' Honour the constructor's preselect hint if it
                    ' resolves to a known node; otherwise default to
                    ' the first entry (matching the legacy behaviour).
                    Dim idx = -1
                    If Not String.IsNullOrEmpty(_preselectedNodeId) Then
                        idx = _nodeEntities.FindIndex(
                            Function(n) String.Equals(n.NodeId,
                                                        _preselectedNodeId,
                                                        StringComparison.Ordinal))
                    End If
                    _nodeComboBox.SelectedIndex = If(idx >= 0, idx, 0)
                End If
            End Using
        End Sub

        Private Sub LoadGames()
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            Dim plugins = registry.GetAllPlugins()
            _gameComboBox.Items.Clear()
            For Each gamePlugin In plugins
                _gameComboBox.Items.Add($"{gamePlugin.DisplayName} ({gamePlugin.GameId})")
            Next
            If _gameComboBox.Items.Count > 0 Then
                _gameComboBox.SelectedIndex = 0
            End If
        End Sub

        Private Sub LoadSteamCredentials()
            _steamCredComboBox.Items.Clear()
            _steamCredIds.Clear()

            ' Add "Anonymous" as first option
            _steamCredComboBox.Items.Add("(Anonymous — no login)")
            _steamCredIds.Add("")

            Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
            If credService Is Nothing Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each entity In credService.ListSteamCredentials(db)
                    _steamCredComboBox.Items.Add($"{entity.DisplayName} ({entity.Username})")
                    _steamCredIds.Add(entity.CredentialId)
                Next
            End Using

            _steamCredComboBox.SelectedIndex = 0
        End Sub

        Private Sub OnNodeChanged(sender As Object, e As EventArgs)
            ' Drop the cached status so the next path-suggestion
            ' fetch grabs the new node's value rather than reusing
            ' the previous selection's.
            _cachedNodeStatus = Nothing
            Task.Run(Async Function()
                         Await RefreshSuggestedInstallPathAsync()
                     End Function)
        End Sub

        Private Sub OnGameChanged(sender As Object, e As EventArgs)
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            Dim plugins = registry.GetAllPlugins()
            If _gameComboBox.SelectedIndex < 0 OrElse
               _gameComboBox.SelectedIndex >= plugins.Count Then Return

            Dim currentPlugin = plugins(_gameComboBox.SelectedIndex)

            ' Update install methods
            _methodComboBox.Items.Clear()
            Dim methods = currentPlugin.GetSupportedInstallMethods()
            For Each installMethod In methods
                _methodComboBox.Items.Add(installMethod.ToString())
            Next
            If _methodComboBox.Items.Count > 0 Then
                _methodComboBox.SelectedIndex = 0
            End If

            ' Update config panel with plugin schema
            _configPanel.Controls.Clear()
            Dim schema = currentPlugin.GetInstallConfigSchema()
            _schemaResult = SchemaFormBuilder.Build(schema, New Dictionary(Of String, String))
            If _schemaResult.Panel IsNot Nothing Then
                _schemaResult.Panel.Dock = DockStyle.Fill
                _configPanel.Controls.Add(_schemaResult.Panel)
            End If

            ' Refresh the notices panel for the new plugin. Plugins
            ' that don't implement IInstallationNoticeProvider get
            ' an empty list here and the panel hides itself.
            Dim notices As IReadOnlyList(Of InstallationNotice) = Nothing
            Dim provider = TryCast(currentPlugin, IInstallationNoticeProvider)
            If provider IsNot Nothing Then
                Try
                    notices = provider.GetPreInstallNotices()
                Catch
                    ' Plugin throwing during a UI-triggered notice
                    ' fetch shouldn't take the form down. Treat as
                    ' "no notices" and move on.
                    notices = Nothing
                End Try
            End If
            RebuildNoticesPanel(notices)

            ' Re-suggest the install path with the new game's GameId
            ' appended to the cached node status (no re-fetch needed
            ' since the node hasn't changed).
            Task.Run(Async Function()
                         Await RefreshSuggestedInstallPathAsync()
                     End Function)
        End Sub

        ''' <summary>
        ''' Replace the contents of _noticesPanel with rendered
        ''' versions of the supplied notices, then reposition the
        ''' Save/Cancel buttons to sit directly below the resized
        ''' panel. Hides the panel entirely when notices is null
        ''' or empty, in which case the buttons return to their
        ''' original position (just below the config panel).
        '''
        ''' Layout per notice:
        '''   - 4px coloured accent bar on the left (orange for
        '''     Warning, gray for Information) so users can scan
        '''     severity at a glance without reading.
        '''   - Optional bold Title line at the top.
        '''   - Body label below the title (or at the top if no
        '''     Title), wrapped at the panel width minus the accent
        '''     bar and some padding.
        '''   - 8px gap between consecutive notices.
        '''
        ''' AutoSize on the body label drives the per-notice height;
        ''' we sum those plus title + padding to get the total
        ''' panel height.
        ''' </summary>
        Private Sub RebuildNoticesPanel(notices As IReadOnlyList(Of InstallationNotice))
            _noticesPanel.SuspendLayout()
            Try
                _noticesPanel.Controls.Clear()

                If notices Is Nothing OrElse notices.Count = 0 Then
                    _noticesPanel.Size = New Size(540, 0)
                    _noticesPanel.Visible = False
                    RepositionButtons(0)
                    Return
                End If

                Const PanelInnerWidth As Integer = 540
                Const AccentBarWidth As Integer = 4
                Const TextLeftPadding As Integer = 12  ' from accent bar
                Const RightPadding As Integer = 8
                Const NoticeGap As Integer = 8
                Const NoticeInnerVPad As Integer = 6
                Dim textMaxWidth = PanelInnerWidth - AccentBarWidth - TextLeftPadding - RightPadding

                Dim cursorY As Integer = 0
                For Each n In notices
                    If n Is Nothing Then Continue For
                    If String.IsNullOrEmpty(n.Body) AndAlso String.IsNullOrEmpty(n.Title) Then Continue For

                    ' Per-notice container so we can compute its
                    ' height once all child labels have laid out.
                    Dim noticeBox As New Panel() With {
                        .Location = New Point(0, cursorY),
                        .Width = PanelInnerWidth,
                        .BackColor = If(n.Severity = NoticeSeverity.Warning,
                                         Color.FromArgb(255, 248, 235),
                                         Color.FromArgb(245, 247, 250))
                    }

                    ' Accent bar — thin coloured strip on the left edge.
                    Dim accent As New Panel() With {
                        .Location = New Point(0, 0),
                        .Size = New Size(AccentBarWidth, 0),
                        .BackColor = If(n.Severity = NoticeSeverity.Warning,
                                         Color.FromArgb(220, 130, 30),
                                         Color.FromArgb(140, 150, 165))
                    }
                    noticeBox.Controls.Add(accent)

                    Dim textY As Integer = NoticeInnerVPad
                    Dim textX As Integer = AccentBarWidth + TextLeftPadding

                    If Not String.IsNullOrEmpty(n.Title) Then
                        Dim titleLbl As New Label() With {
                            .Text = n.Title,
                            .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                            .ForeColor = Color.FromArgb(40, 40, 50),
                            .Location = New Point(textX, textY),
                            .MaximumSize = New Size(textMaxWidth, 0),
                            .AutoSize = True
                        }
                        noticeBox.Controls.Add(titleLbl)
                        textY += titleLbl.Height + 2
                    End If

                    If Not String.IsNullOrEmpty(n.Body) Then
                        Dim bodyLbl As New Label() With {
                            .Text = n.Body,
                            .Font = New Font("Segoe UI", 9),
                            .ForeColor = Color.FromArgb(60, 60, 70),
                            .Location = New Point(textX, textY),
                            .MaximumSize = New Size(textMaxWidth, 0),
                            .AutoSize = True
                        }
                        noticeBox.Controls.Add(bodyLbl)
                        textY += bodyLbl.Height
                    End If

                    Dim noticeHeight = textY + NoticeInnerVPad
                    noticeBox.Height = noticeHeight
                    accent.Size = New Size(AccentBarWidth, noticeHeight)

                    _noticesPanel.Controls.Add(noticeBox)
                    cursorY += noticeHeight + NoticeGap
                Next

                ' Strip the trailing gap from the total height —
                ' it was added after every notice including the last
                ' one, but there's nothing below the last notice.
                Dim totalHeight = Math.Max(0, cursorY - NoticeGap)
                _noticesPanel.Size = New Size(PanelInnerWidth, totalHeight)
                _noticesPanel.Visible = totalHeight > 0
                RepositionButtons(If(totalHeight > 0, totalHeight + 12, 0))
            Finally
                _noticesPanel.ResumeLayout()
            End Try
        End Sub

        ''' <summary>
        ''' Move the Save/Cancel buttons to sit `extraOffset` pixels
        ''' below the notices base position. extraOffset = 0 puts
        ''' them at the original location (just below the config
        ''' panel) for the no-notices case; positive values push
        ''' them down to clear the notices panel.
        ''' </summary>
        Private Sub RepositionButtons(extraOffset As Integer)
            Dim buttonY = _noticesBaseY + extraOffset
            If _saveButton IsNot Nothing Then
                _saveButton.Location = New Point(_saveButton.Location.X, buttonY)
            End If
            If _cancelButton IsNot Nothing Then
                _cancelButton.Location = New Point(_cancelButton.Location.X, buttonY)
            End If
        End Sub

        Private Sub OnPathTextChanged(sender As Object, e As EventArgs)
            ' Programmatic writes go through _suppressPathChange so
            ' the auto-suggest doesn't trigger its own "user edited"
            ' flag and lock itself out on the very first suggestion.
            If _suppressPathChange Then Return
            _pathUserEdited = True
        End Sub

        ''' <summary>
        ''' Fetch the selected node's status (cached if already
        ''' fetched for this node) and update the install path
        ''' suggestion. Bails out cleanly if the user has manually
        ''' edited the path field, or if no node/game is selected
        ''' yet, or if the node can't be reached.
        '''
        ''' Runs on a background task so the UI stays responsive
        ''' during the HTTP round trip; marshals the actual control
        ''' update back to the UI thread.
        ''' </summary>
        Private Async Function RefreshSuggestedInstallPathAsync() As Task
            If _pathUserEdited Then Return

            ' Capture selections on the UI thread so the background
            ' work has consistent inputs even if the user changes
            ' selection mid-fetch.
            Dim selectedNodeIdx As Integer = -1
            Dim selectedGameIdx As Integer = -1
            Try
                If Me.IsDisposed Then Return
                Me.Invoke(Sub()
                              selectedNodeIdx = _nodeComboBox.SelectedIndex
                              selectedGameIdx = _gameComboBox.SelectedIndex
                          End Sub)
            Catch
                Return
            End Try

            If selectedNodeIdx < 0 OrElse selectedNodeIdx >= _nodeEntities.Count Then Return
            If selectedGameIdx < 0 Then Return

            Dim nodeEntity = _nodeEntities(selectedNodeIdx)

            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return
            Dim plugins = registry.GetAllPlugins()
            If selectedGameIdx >= plugins.Count Then Return
            Dim plugin = plugins(selectedGameIdx)
            Dim gameId = plugin.GameId

            ' Fetch (or re-use cached) node status. Cancel any
            ' in-flight fetch from a prior selection.
            Dim status As NodeStatusResponse = _cachedNodeStatus
            If status Is Nothing Then
                _statusFetchCts?.Cancel()
                _statusFetchCts = New CancellationTokenSource()
                Dim token = _statusFetchCts.Token
                Try
                    Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
                    If factory Is Nothing Then Return
                    Dim client = factory.GetClient(
                        nodeEntity.NodeId, nodeEntity.HostAddress,
                        nodeEntity.Port, nodeEntity.AuthToken)
                    status = Await client.GetStatusAsync(token)
                    _cachedNodeStatus = status
                Catch
                    ' Node unreachable / auth failure / etc. — fall
                    ' through to the generic placeholder branch
                    ' below. Don't surface as an error; the user can
                    ' just type a path manually.
                    status = Nothing
                End Try
            End If

            Dim suggestedPath = BuildSuggestedPath(status, gameId)
            If String.IsNullOrEmpty(suggestedPath) Then Return

            ' Marshal the assignment to the UI thread under the
            ' suppress flag so we don't accidentally mark this as a
            ' user edit.
            If Me.IsDisposed Then Return
            Me.BeginInvoke(Sub()
                               If _pathUserEdited Then Return
                               _suppressPathChange = True
                               Try
                                   _pathTextBox.Text = suggestedPath
                               Finally
                                   _suppressPathChange = False
                               End Try
                           End Sub)
        End Function

        ''' <summary>
        ''' Combine the node-reported ServersDirectory with the
        ''' selected game's GameId. Uses whichever path separator
        ''' the ServersDirectory itself uses so a Windows manager
        ''' talking to a Linux node still produces a path the node
        ''' will accept (forward slashes both for Linux paths and
        ''' as a Windows-tolerated alternative).
        '''
        ''' If the node didn't report a ServersDirectory (older
        ''' node version, fetch failed, etc.), returns Nothing —
        ''' caller should leave the field blank and let the user
        ''' type it manually.
        ''' </summary>
        Private Shared Function BuildSuggestedPath(status As NodeStatusResponse,
                                                     gameId As String) As String
            If status Is Nothing Then Return Nothing
            Dim baseDir = status.ServersDirectory
            If String.IsNullOrEmpty(baseDir) Then Return Nothing
            If String.IsNullOrEmpty(gameId) Then Return baseDir

            ' Pick separator from the base. If the base contains
            ' a backslash we're talking to a Windows node; otherwise
            ' assume forward slash (works on Linux and is tolerated
            ' on Windows for non-UNC paths).
            Dim sep = If(baseDir.Contains("\"c), "\", "/")
            ' Strip any trailing separator so we don't double up.
            Dim trimmed = baseDir.TrimEnd("/"c, "\"c)
            Return trimmed & sep & gameId
        End Function

        Private Sub OnSave(sender As Object, e As EventArgs)
            ' Validate
            If _nodeComboBox.SelectedIndex < 0 Then
                MessageBox.Show("Select a node.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            If _gameComboBox.SelectedIndex < 0 Then
                MessageBox.Show("Select a game.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            If String.IsNullOrWhiteSpace(_pathTextBox.Text) Then
                MessageBox.Show("Install path is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            Dim plugins = registry.GetAllPlugins()
            Dim chosenPlugin = plugins(_gameComboBox.SelectedIndex)
            Dim selectedNode = _nodeEntities(_nodeComboBox.SelectedIndex)

            ' Display name fallback: blank field uses the plugin's
            ' DisplayName. Trim first so a name of all whitespace
            ' gets the same treatment as truly empty.
            Dim displayName = If(_nameTextBox.Text, "").Trim()
            If String.IsNullOrEmpty(displayName) Then
                displayName = chosenPlugin.DisplayName
            End If

            ' Collect config values
            Dim configValues As New Dictionary(Of String, String)
            If _schemaResult IsNot Nothing AndAlso _schemaResult.ValueExtractor IsNot Nothing Then
                configValues = _schemaResult.ValueExtractor.Invoke()
            End If

            Dim installId As String

            ' Capture selected credential on UI thread
            Dim selectedCredId = ""
            If _steamCredComboBox.SelectedIndex > 0 AndAlso
               _steamCredComboBox.SelectedIndex < _steamCredIds.Count Then
                selectedCredId = _steamCredIds(_steamCredComboBox.SelectedIndex)
            End If

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Create installation
                installId = Guid.NewGuid().ToString("N")
                Dim installEntity As New InstallationEntity With {
                    .InstallationId = installId,
                    .GameId = chosenPlugin.GameId,
                    .DisplayName = displayName,
                    .NodeId = selectedNode.NodeId,
                    .InstallPath = _pathTextBox.Text.Trim(),
                    .InstallMethod = If(_methodComboBox.SelectedItem IsNot Nothing,
                                        _methodComboBox.SelectedItem.ToString(), "Manual"),
                    .SteamCredentialId = selectedCredId,
                    .ConfigJson = JsonSerializer.Serialize(configValues),
                    .RunCommonRedist = _runRedistCheckBox.Checked,
                    .CreatedUtc = DateTime.UtcNow,
                    .UpdatedUtc = DateTime.UtcNow
                }
                db.Installations.Add(installEntity)

                ' Optionally create first instance
                If _createInstanceCheckBox.Checked AndAlso
                   Not String.IsNullOrWhiteSpace(_instanceNameTextBox.Text) Then

                    ' Build the instance's config:
                    '  1. Inherit any keys set at install level (so
                    '     LO's CustomerKey/ProviderKey etc. flow into
                    '     the first instance's merged-config view).
                    '  2. Run PortAllocator and overlay its
                    '     suggestions for port-typed fields. The
                    '     allocator does global node-wide collision
                    '     checking, so the first instance of a
                    '     SECOND installation on the same node
                    '     correctly gets ports that don't collide
                    '     with the first installation's instances.
                    '  3. Fill any remaining port-typed fields with
                    '     the schema's DefaultValue. The allocator
                    '     can return no suggestion when a field's
                    '     port range is exhausted; without this step
                    '     the saved instance config wouldn't carry
                    '     a value for that key, and the validator
                    '     below couldn't see what port the runtime
                    '     would actually use (plugin GetFieldInt
                    '     fallback). Filling the default explicitly
                    '     lets the validator catch the conflict.
                    Dim instanceConfig As New Dictionary(Of String, String)(
                        configValues, StringComparer.OrdinalIgnoreCase)
                    Dim portSuggestions = PortAllocator.SuggestPortsForNewInstance(
                        chosenPlugin, selectedNode.NodeId, db)
                    For Each kvp In portSuggestions
                        instanceConfig(kvp.Key) = kvp.Value
                    Next
                    Dim instanceSchema = chosenPlugin.GetInstanceConfigSchema()
                    If instanceSchema IsNot Nothing Then
                        For Each f In instanceSchema
                            If f Is Nothing OrElse Not f.IsPort Then Continue For
                            Dim existing As String = Nothing
                            If instanceConfig.TryGetValue(f.Key, existing) AndAlso
                               Not String.IsNullOrWhiteSpace(existing) Then
                                Continue For
                            End If
                            If Not String.IsNullOrEmpty(f.DefaultValue) Then
                                instanceConfig(f.Key) = f.DefaultValue
                            End If
                        Next
                    End If

                    ' Validate the assembled instance config against
                    ' every other instance on the node. The suggester
                    ' avoids collisions by construction, but only for
                    ' fields it could allocate — fields filled from
                    ' schema defaults above can still collide. Same
                    ' warn-and-confirm policy as AddInstanceForm.
                    ' Genuinely-first-installation case: the validator
                    ' returns an empty list (nothing to collide with),
                    ' so the dialog doesn't fire and there's no
                    ' friction. The check is only visible when there's
                    ' actually something to flag.
                    Dim conflicts = PortAllocator.FindPortConflicts(
                        chosenPlugin, selectedNode.NodeId, "", instanceConfig, db)
                    If conflicts.Count > 0 Then
                        Dim msg = "Port conflicts detected for the new instance:" & vbCrLf & vbCrLf &
                            PortAllocator.FormatConflictsForDisplay(conflicts) & vbCrLf &
                            "Conflicting ports will fail to bind when both servers run at the same time." & vbCrLf &
                            "Save anyway?"
                        Dim res = MessageBox.Show(msg, "Port Conflicts",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                        If res <> DialogResult.Yes Then
                            ' User declined. Returning here drops both
                            ' the in-memory installEntity Add and the
                            ' yet-to-be-added instanceEntity — the
                            ' Using scope disposes the DbContext
                            ' without SaveChanges, so nothing persists.
                            ' The form stays open so the user can
                            ' adjust their input and click Create
                            ' again.
                            Return
                        End If
                    End If

                    Dim instanceEntity As New InstanceEntity With {
                        .InstanceId = Guid.NewGuid().ToString("N"),
                        .InstallationId = installId,
                        .GameId = chosenPlugin.GameId,
                        .DisplayName = _instanceNameTextBox.Text.Trim(),
                        .ConfigJson = JsonSerializer.Serialize(instanceConfig),
                        .CreatedUtc = DateTime.UtcNow,
                        .UpdatedUtc = DateTime.UtcNow
                    }
                    db.Instances.Add(instanceEntity)
                End If

                db.SaveChanges()
            End Using

            ' Ask whether to run the install now
            Dim runNow = MessageBox.Show(
                "Installation record created. Run the install on the node now?" & vbCrLf & vbCrLf &
                "This will download the game server files to the specified path.",
                "Run Install?", MessageBoxButtons.YesNo, MessageBoxIcon.Question)

            If runNow = DialogResult.Yes Then
                ' Fire install in background
                Dim installMgr = ManagerProgram.Services.GetService(Of InstallationManager)()
                If installMgr IsNot Nothing Then
                    _saveButton.Enabled = False
                    _saveButton.Text = "Installing..."

                    Task.Run(Async Function()
                                 Try
                                     Dim ok = Await installMgr.InstallAsync(
                                         installId, selectedCredId,
                                         promptHandler:=AddressOf HandleSteamPrompt)
                                     Me.BeginInvoke(Sub()
                                                        If ok Then
                                                            MessageBox.Show("Installation completed successfully!",
                                                                          "Install Complete", MessageBoxButtons.OK,
                                                                          MessageBoxIcon.Information)
                                                        Else
                                                            MessageBox.Show("Installation failed. Check the node logs for details.",
                                                                          "Install Failed", MessageBoxButtons.OK,
                                                                          MessageBoxIcon.Warning)
                                                        End If
                                                        Me.DialogResult = DialogResult.OK
                                                        Me.Close()
                                                    End Sub)
                                 Catch ex As Exception
                                     Me.BeginInvoke(Sub()
                                                        MessageBox.Show($"Installation error: {ex.Message}" & vbCrLf & vbCrLf &
                                                                       $"Details: {ex.ToString()}",
                                                                       "Install Error", MessageBoxButtons.OK,
                                                                       MessageBoxIcon.Error)
                                                        _saveButton.Enabled = True
                                                        _saveButton.Text = "Create"
                                                    End Sub)
                                 End Try
                             End Function)
                    Return ' Don't close yet — wait for install
                End If
            End If

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Function AddLabel(text As String, x As Integer, y As Integer) As Label
            Dim lbl As New Label()
            lbl.Text = text
            lbl.AutoSize = True
            lbl.Location = New Point(x, y + 3)
            Me.Controls.Add(lbl)
            Return lbl
        End Function

        Private Function HandleSteamPrompt(promptType As PromptType,
                                            message As String) As Task(Of String)
            ' Marshal to UI thread to show input dialog
            Dim result As String = Nothing
            Me.Invoke(Sub()
                          Dim title = If(promptType = PromptType.TwoFactorCode,
                              "Steam Mobile Authenticator", "Steam Guard Code")
                          Dim prompt = If(String.IsNullOrEmpty(message),
                              "Enter the code from your email or authenticator app:",
                              message)
                          result = Microsoft.VisualBasic.Interaction.InputBox(
                              prompt, title, "")
                      End Sub)
            Return Task.FromResult(result)
        End Function

    End Class

End Namespace
