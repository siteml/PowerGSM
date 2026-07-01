Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Notification

' ============================================================
'  NotificationsForm — manages Discord webhook destinations.
'
'  Layout: ListView on the left showing all destinations, a
'  details panel on the right for editing the selected one.
'  Changes are only written to the DB on Save; the form keeps
'  an in-memory edit model so the user can cancel.
'
'  Once saved, we call DiscordWebhookPlugin.RefreshConfigAsync
'  so the live plugin picks up the changes without a restart.
' ============================================================

Namespace GSM.Manager.UI

    Public Class NotificationsForm
        Inherits Form

        Private _destList As ListView
        Private _detailsPanel As Panel
        Private _nameTextBox As TextBox
        Private _transportCombo As ComboBox
        Private _webhookTextBox As TextBox
        Private _webhookLabel As Label
        Private _guildLabel As Label
        Private _guildCombo As ComboBox
        Private _channelLabel As Label
        Private _channelCombo As ComboBox
        Private _enabledCheckBox As CheckBox
        Private _scopeHost As Panel
        Private _scopeHint As Label
        Private _lowerHost As Panel
        Private _lowerHostTop As Integer
        Private _eventsPanel As Panel
        Private _visibilityPanel As Panel
        Private _nodeSection As CollapsibleCheckSection
        Private _installSection As CollapsibleCheckSection
        Private _instanceSection As CollapsibleCheckSection
        Private _setSection As CollapsibleCheckSection
        Private _matchLabel As Label
        Private _eventChecks As Dictionary(Of NotificationEventType, CheckBox)
        Private _profileCombo As ComboBox
        Private _manageProfilesButton As Button
        Private _customizeTemplatesButton As Button
        Private _testButton As Button
        Private _addButton As Button
        Private _removeButton As Button
        Private _saveButton As Button
        Private _closeButton As Button

        ' Edit model — in-memory working copies until Save.
        Private _destinations As New List(Of DestinationEdit)
        Private _selectedDestination As DestinationEdit
        Private _allInstallations As New List(Of InstallationEntity)
        ' Live bot's known guilds + channels, populated in
        ' LoadDataAsync if the bot is connected. Empty list is
        ' valid — the form falls back to a placeholder "connect
        ' the bot first" entry in that case (matches
        ' DiscordPanelEditorForm's pattern).
        Private _botGuilds As IReadOnlyList(Of GuildInfo) = New List(Of GuildInfo)
        Private _suppressEvents As Boolean = False

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            LoadDataAsync()
        End Sub

        ' ---- UI construction ----

        Private Sub InitializeControls()
            Me.Text = "Notifications"
            Me.Size = New Size(980, 720)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.MinimumSize = New Size(900, 600)

            ' Left pane: destination list + Add/Remove/Test buttons
            Dim leftPanel As New Panel() With {.Dock = DockStyle.Left, .Width = 260, .Padding = New Padding(8)}

            _destList = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .HideSelection = False,
                .MultiSelect = False,
                .CheckBoxes = False
            }
            _destList.Columns.Add("Destination", 200)
            _destList.Columns.Add("Transport", 70)
            AddHandler _destList.SelectedIndexChanged, AddressOf OnDestinationSelected

            Dim leftButtonRow As New Panel() With {.Dock = DockStyle.Bottom, .Height = 40}
            _addButton = New Button() With {.Text = "Add", .Location = New Point(0, 5), .Size = New Size(70, 28)}
            AddHandler _addButton.Click, AddressOf OnAddClicked
            _removeButton = New Button() With {.Text = "Remove", .Location = New Point(75, 5), .Size = New Size(70, 28)}
            AddHandler _removeButton.Click, AddressOf OnRemoveClicked
            _testButton = New Button() With {.Text = "Test", .Location = New Point(150, 5), .Size = New Size(70, 28)}
            AddHandler _testButton.Click, AddressOf OnTestClicked
            leftButtonRow.Controls.AddRange(New Control() {_addButton, _removeButton, _testButton})

            leftPanel.Controls.Add(_destList)
            leftPanel.Controls.Add(leftButtonRow)

            ' Right pane: details editor
            _detailsPanel = New Panel() With {
                .Dock = DockStyle.Fill,
                .Padding = New Padding(16, 8, 16, 8),
                .AutoScroll = True
            }
            BuildDetailsPanel()

            ' Bottom: Save/Close
            Dim footer As New Panel() With {.Dock = DockStyle.Bottom, .Height = 48, .Padding = New Padding(8)}
            _saveButton = New Button() With {.Text = "Save", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            AddHandler _saveButton.Click, AddressOf OnSaveClicked
            _closeButton = New Button() With {.Text = "Close", .Size = New Size(100, 30), .Dock = DockStyle.Right}
            _closeButton.DialogResult = DialogResult.Cancel
            footer.Controls.Add(_saveButton)
            footer.Controls.Add(_closeButton)
            Me.CancelButton = _closeButton

            Me.Controls.Add(_detailsPanel)
            Me.Controls.Add(leftPanel)
            Me.Controls.Add(footer)
        End Sub

        Private Sub BuildDetailsPanel()
            Dim y = 8

            AddSectionHeader("Destination", y) : y += 28

            AddFieldLabel("Name:", y)
            _nameTextBox = New TextBox() With {.Location = New Point(150, y), .Size = New Size(440, 24)}
            AddHandler _nameTextBox.TextChanged, AddressOf OnNameChanged
            _detailsPanel.Controls.Add(_nameTextBox)
            y += 32

            ' Transport selector — Webhook (HTTP POST) vs Bot
            ' (DSharpPlus client send). Stored in DestinationEdit
            ' as the canonical TransportKind string written to the
            ' DB on save ("DiscordWebhook" or "DiscordBot").
            AddFieldLabel("Transport:", y)
            _transportCombo = New ComboBox() With {
                .Location = New Point(150, y), .Size = New Size(220, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            _transportCombo.Items.Add(New TransportItem("DiscordWebhook", "Discord Webhook"))
            _transportCombo.Items.Add(New TransportItem("DiscordBot", "Discord Bot"))
            AddHandler _transportCombo.SelectedIndexChanged, AddressOf OnTransportChanged
            _detailsPanel.Controls.Add(_transportCombo)
            y += 32

            ' Reserve two rows for transport-specific config so the
            ' subsequent content sits at a stable y regardless of
            ' which transport is selected:
            '   row 1: Webhook URL (when DiscordWebhook)
            '          OR Guild dropdown (when DiscordBot)
            '   row 2: empty (when DiscordWebhook)
            '          OR Channel dropdown (when DiscordBot)
            ' OnTransportChanged toggles visibility on the relevant
            ' set; the hidden controls keep their layout slots so
            ' the form doesn't reflow on transport change.
            Dim transportRowY = y

            ' Webhook URL field — same position as Guild combo;
            ' visibility toggled by OnTransportChanged.
            _webhookLabel = New Label() With {
                .Text = "Webhook URL:", .AutoSize = True,
                .Location = New Point(20, transportRowY + 4)
            }
            _detailsPanel.Controls.Add(_webhookLabel)
            _webhookTextBox = New TextBox() With {.Location = New Point(150, transportRowY), .Size = New Size(440, 24)}
            AddHandler _webhookTextBox.TextChanged, AddressOf OnWebhookChanged
            _detailsPanel.Controls.Add(_webhookTextBox)

            ' Guild combo at the same y as Webhook URL.
            _guildLabel = New Label() With {
                .Text = "Guild:", .AutoSize = True,
                .Location = New Point(20, transportRowY + 4)
            }
            _detailsPanel.Controls.Add(_guildLabel)
            _guildCombo = New ComboBox() With {
                .Location = New Point(150, transportRowY), .Size = New Size(440, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _guildCombo.SelectedIndexChanged, AddressOf OnGuildChangedNotif
            _detailsPanel.Controls.Add(_guildCombo)

            ' Channel combo on the second reserved row.
            _channelLabel = New Label() With {
                .Text = "Channel:", .AutoSize = True,
                .Location = New Point(20, transportRowY + 32 + 4)
            }
            _detailsPanel.Controls.Add(_channelLabel)
            _channelCombo = New ComboBox() With {
                .Location = New Point(150, transportRowY + 32), .Size = New Size(440, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _channelCombo.SelectedIndexChanged, AddressOf OnChannelChangedNotif
            _detailsPanel.Controls.Add(_channelCombo)

            ' Skip past the two reserved rows.
            y += 64

            _enabledCheckBox = New CheckBox() With {
                .Text = "Enabled", .AutoSize = True,
                .Location = New Point(150, y)
            }
            AddHandler _enabledCheckBox.CheckedChanged, AddressOf OnEnabledCheckBoxChanged
            _detailsPanel.Controls.Add(_enabledCheckBox)
            y += 34

            ' Phase 5n — scope / events / visibility share one growing,
            ' panel-scrolled column. The sections dock-stack so they
            ' reflow as the scope box grows; the details panel scrolls
            ' the whole column (nothing inside scrolls on its own).
            _lowerHostTop = y
            _lowerHost = New Panel() With {.Location = New Point(0, y)}
            _detailsPanel.Controls.Add(_lowerHost)
            AddHandler _detailsPanel.SizeChanged, AddressOf OnDetailsResize

            ' Scope intro: header + hint + live match count.
            Dim scopeIntro As New Panel() With {.Dock = DockStyle.Top, .Height = 66}
            Dim scopeHeader As New Label() With {
                .Text = "Scope", .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(50, 50, 120), .AutoSize = True,
                .Location = New Point(0, 2)
            }
            _scopeHint = New Label() With {
                .Text = "An event is in scope if it matches any checked dimension below. Nothing checked anywhere = all instances.",
                .Location = New Point(20, 24), .Size = New Size(540, 16), .AutoSize = False,
                .ForeColor = Color.DimGray, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic)
            }
            _matchLabel = New Label() With {
                .Location = New Point(20, 44), .AutoSize = True,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold), .ForeColor = Color.FromArgb(50, 50, 120)
            }
            scopeIntro.Controls.AddRange(New Control() {scopeHeader, _scopeHint, _matchLabel})

            ' Scope box — grows to fit its four dimension sections.
            _scopeHost = New Panel() With {
                .Dock = DockStyle.Top, .Height = 100,
                .BorderStyle = BorderStyle.FixedSingle, .BackColor = Color.White
            }
            _nodeSection = New CollapsibleCheckSection("Nodes")
            _installSection = New CollapsibleCheckSection("Installations")
            _instanceSection = New CollapsibleCheckSection("Instances")
            _setSection = New CollapsibleCheckSection("Instance sets")
            AddHandler _nodeSection.CheckedChanged, AddressOf OnScopeNodeChanged
            AddHandler _installSection.CheckedChanged, AddressOf OnScopeInstallationChanged
            AddHandler _instanceSection.CheckedChanged, AddressOf OnScopeInstanceChanged
            AddHandler _setSection.CheckedChanged, AddressOf OnScopeSetChanged
            AddHandler _nodeSection.ExpandedChanged, AddressOf OnScopeSectionExpanded
            AddHandler _installSection.ExpandedChanged, AddressOf OnScopeSectionExpanded
            AddHandler _instanceSection.ExpandedChanged, AddressOf OnScopeSectionExpanded
            AddHandler _setSection.ExpandedChanged, AddressOf OnScopeSectionExpanded
            ' Dock.Top stacks in reverse add-order; add bottom-first so
            ' Nodes ends up on top.
            _scopeHost.Controls.Add(_setSection)
            _scopeHost.Controls.Add(_instanceSection)
            _scopeHost.Controls.Add(_installSection)
            _scopeHost.Controls.Add(_nodeSection)

            ' Events panel.
            _eventsPanel = New Panel() With {.Dock = DockStyle.Top, .Height = 116}
            Dim eventsHeader As New Label() With {
                .Text = "Events", .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(50, 50, 120), .AutoSize = True, .Location = New Point(0, 8)
            }
            _eventsPanel.Controls.Add(eventsHeader)
            BuildEventCheckboxes(_eventsPanel, 36)

            ' Visibility & templates panel.
            _visibilityPanel = New Panel() With {.Dock = DockStyle.Top, .Height = 108}
            Dim visHeader As New Label() With {
                .Text = "Visibility & templates", .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(50, 50, 120), .AutoSize = True, .Location = New Point(0, 8)
            }
            _visibilityPanel.Controls.Add(visHeader)
            Dim profileLbl As New Label() With {.Text = "Profile:", .AutoSize = True, .Location = New Point(20, 42)}
            _profileCombo = New ComboBox() With {
                .Location = New Point(150, 38), .Size = New Size(220, 24), .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _profileCombo.SelectedIndexChanged, AddressOf OnProfileChanged
            _manageProfilesButton = New Button() With {
                .Text = "Manage Profiles...", .Location = New Point(380, 37), .Size = New Size(140, 26)
            }
            AddHandler _manageProfilesButton.Click, AddressOf OnManageProfilesClicked
            Dim templatesLbl As New Label() With {.Text = "Templates:", .AutoSize = True, .Location = New Point(20, 76)}
            _customizeTemplatesButton = New Button() With {
                .Text = "Customize Message Templates...", .Location = New Point(150, 72), .Size = New Size(240, 26)
            }
            AddHandler _customizeTemplatesButton.Click, AddressOf OnCustomizeTemplatesClicked
            _visibilityPanel.Controls.AddRange(New Control() {
                profileLbl, _profileCombo, _manageProfilesButton, templatesLbl, _customizeTemplatesButton})

            ' Dock.Top reverse add-order: intro → Scope → Events → Visibility.
            _lowerHost.Controls.Add(_visibilityPanel)
            _lowerHost.Controls.Add(_eventsPanel)
            _lowerHost.Controls.Add(_scopeHost)
            _lowerHost.Controls.Add(scopeIntro)

            RelayoutScope()
            OnDetailsResize(Nothing, EventArgs.Empty)
        End Sub

        Private Sub AddSectionHeader(text As String, y As Integer)
            Dim lbl As New Label() With {
                .Text = text, .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(0, y),
                .ForeColor = Color.FromArgb(50, 50, 120)
            }
            _detailsPanel.Controls.Add(lbl)
        End Sub

        Private Sub AddFieldLabel(text As String, y As Integer)
            Dim lbl As New Label() With {
                .Text = text, .AutoSize = True,
                .Location = New Point(20, y + 4)
            }
            _detailsPanel.Controls.Add(lbl)
        End Sub

        Private Sub BuildEventCheckboxes(container As Control, startY As Integer)
            _eventChecks = New Dictionary(Of NotificationEventType, CheckBox)
            ' Only the event types the Discord integration currently
            ' cares about — skip Custom / AutomationRule* / UpdateAvailable
            ' (manual only per user preference) / Node events.
            Dim wanted = {
                NotificationEventType.InstanceStarted,
                NotificationEventType.InstanceStopped,
                NotificationEventType.InstanceCrashed,
                NotificationEventType.CrashLoopDetected,
                NotificationEventType.UpdateStarted,
                NotificationEventType.UpdateCompleted,
                NotificationEventType.UpdateFailed,
                NotificationEventType.PlayerJoined,
                NotificationEventType.PlayerLeft
            }
            Dim col = 0
            Dim row = 0
            For Each evt In wanted
                Dim cb As New CheckBox() With {
                    .Text = PrettifyEventName(evt),
                    .AutoSize = True,
                    .Location = New Point(20 + col * 200, startY + row * 24),
                    .Tag = evt
                }
                AddHandler cb.CheckedChanged, AddressOf OnEventCheckChanged
                container.Controls.Add(cb)
                _eventChecks(evt) = cb
                col += 1
                If col >= 3 Then
                    col = 0
                    row += 1
                End If
            Next
        End Sub

        Private Shared Function PrettifyEventName(e As NotificationEventType) As String
            Select Case e
                Case NotificationEventType.InstanceStarted : Return "Server started"
                Case NotificationEventType.InstanceStopped : Return "Server stopped"
                Case NotificationEventType.InstanceCrashed : Return "Server crashed"
                Case NotificationEventType.CrashLoopDetected : Return "Crash loop halted"
                Case NotificationEventType.UpdateStarted : Return "Update started"
                Case NotificationEventType.UpdateCompleted : Return "Update completed"
                Case NotificationEventType.UpdateFailed : Return "Update failed"
                Case NotificationEventType.PlayerJoined : Return "Player joined"
                Case NotificationEventType.PlayerLeft : Return "Player left"
                Case Else : Return e.ToString()
            End Select
        End Function

        ' ---- Data loading ----

        Private Async Sub LoadDataAsync()
            Try
                ' Pull live guild list from the bot plugin if it's
                ' connected — used to populate the Guild dropdown
                ' for DiscordBot-transport destinations. If the
                ' plugin is missing or disconnected, fall back to
                ' an empty list and the dropdown shows a "(connect
                ' the bot first)" placeholder.
                Try
                    Dim botPlugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
                    If botPlugin IsNot Nothing AndAlso botPlugin.IsConnected Then
                        _botGuilds = botPlugin.GetGuildsAndChannels()
                    End If
                Catch
                    ' Bot plugin DI failure is non-fatal here —
                    ' webhook destinations still work.
                End Try

                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' Ensure built-in profiles exist
                    Await EnsureDefaultProfilesAsync(db)

                    _allInstallations = Await db.Installations.
                        Include(Function(i) i.Node).
                        Include(Function(i) i.Instances).
                        ToListAsync()

                    ' Load destinations across BOTH transports.
                    ' The form is the single config surface for
                    ' all event-driven dispatch regardless of
                    ' transport.
                    Dim destEntities = Await db.NotificationDestinations.ToListAsync()
                    _destinations = destEntities.Select(AddressOf DestinationEdit.FromEntity).ToList()

                    ' Populate profile dropdown
                    Dim profiles = Await db.VisibilityProfiles.ToListAsync()
                    _suppressEvents = True
                    _profileCombo.Items.Clear()
                    For Each p In profiles
                        _profileCombo.Items.Add(New ProfileItem(p.ProfileId, p.DisplayName))
                    Next
                    _suppressEvents = False
                End Using

                _suppressEvents = True
                PopulateScopeItems()
                _suppressEvents = False

                RefreshDestinationList()
                If _destinations.Count > 0 Then
                    _destList.Items(0).Selected = True
                Else
                    ClearDetails()
                End If
            Catch ex As Exception
                MessageBox.Show($"Failed to load notifications: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Sub

        Private Async Function EnsureDefaultProfilesAsync(db As GsmDbContext) As Task
            Dim existing = Await db.VisibilityProfiles.AnyAsync()
            If existing Then Return

            Dim publicFields As New List(Of String) From {
                "InstanceName", "NodeName", "InstallationName", "GameName",
                "PlayerName", "PlayerCount", "MaxPlayers",
                "MapPath", "TileName", "MatchState"
            }
            Dim adminFields As New List(Of String) From {
                "InstanceName", "NodeName", "InstallationName", "GameName",
                "PlayerName", "PlayerCount", "MaxPlayers", "PID", "ExitCode",
                "MapPath", "TileName", "TileId", "MatchState", "IPAddress",
                "Port", "InstallPath", "SteamID", "BuildId", "ErrorMessage",
                "CustomerKey", "ProviderKey", "RuleName", "Timestamp",
                "EventType", "Message", "CrashCount", "WindowMinutes"
            }

            db.VisibilityProfiles.Add(New VisibilityProfileEntity With {
                .ProfileId = "public",
                .DisplayName = "Public",
                .AllowedFieldsJson = JsonSerializer.Serialize(publicFields),
                .IsBuiltIn = True,
                .CreatedUtc = DateTime.UtcNow,
                .UpdatedUtc = DateTime.UtcNow
            })
            db.VisibilityProfiles.Add(New VisibilityProfileEntity With {
                .ProfileId = "admin",
                .DisplayName = "Admin (all fields)",
                .AllowedFieldsJson = JsonSerializer.Serialize(adminFields),
                .IsBuiltIn = True,
                .CreatedUtc = DateTime.UtcNow,
                .UpdatedUtc = DateTime.UtcNow
            })
            Await db.SaveChangesAsync()
        End Function

        ' ---- Destination list ----

        Private Sub RefreshDestinationList()
            _suppressEvents = True
            _destList.Items.Clear()
            For Each d In _destinations
                Dim item As New ListViewItem(d.DisplayName)
                item.SubItems.Add(TransportLabelFor(d.TransportKind))
                item.Tag = d
                If Not d.Enabled Then item.ForeColor = Color.Gray
                _destList.Items.Add(item)
            Next
            _suppressEvents = False
        End Sub

        ''' <summary>
        ''' Friendly transport label for the destination list and
        ''' edit-model display. Matches the dropdown text in the
        ''' details panel.
        ''' </summary>
        Private Shared Function TransportLabelFor(kind As String) As String
            If String.Equals(kind, "DiscordBot", StringComparison.OrdinalIgnoreCase) Then Return "Bot"
            ' Default — includes legacy unset / DiscordWebhook.
            Return "Webhook"
        End Function

        Private Sub OnDestinationSelected(sender As Object, e As EventArgs)
            If _suppressEvents Then Return
            If _destList.SelectedItems.Count = 0 Then
                ClearDetails()
                Return
            End If
            _selectedDestination = TryCast(_destList.SelectedItems(0).Tag, DestinationEdit)
            LoadDetailsFromSelection()
        End Sub

        Private Sub LoadDetailsFromSelection()
            _suppressEvents = True
            Try
                If _selectedDestination Is Nothing Then
                    ClearDetails()
                    Return
                End If

                _nameTextBox.Text = If(_selectedDestination.DisplayName, "")

                ' Transport — select the matching item, then swap
                ' UI visibility for that transport. SelectedIndex
                ' assignment doesn't fire when setting to the
                ' current value, so call ApplyTransportVisibility
                ' explicitly.
                Dim transportIdx = 0
                For i = 0 To _transportCombo.Items.Count - 1
                    Dim t = TryCast(_transportCombo.Items(i), TransportItem)
                    If t IsNot Nothing AndAlso String.Equals(t.Kind, _selectedDestination.TransportKind,
                                                              StringComparison.OrdinalIgnoreCase) Then
                        transportIdx = i
                        Exit For
                    End If
                Next
                _transportCombo.SelectedIndex = transportIdx
                ApplyTransportVisibility()

                _webhookTextBox.Text = If(_selectedDestination.WebhookUrl, "")
                PopulateGuildAndChannelCombos(_selectedDestination.GuildId, _selectedDestination.ChannelId)

                _enabledCheckBox.Checked = _selectedDestination.Enabled

                ' Scope — apply the four filter dimensions to the
                ' accordion sections (each set's own comparer decides
                ' membership).
                _nodeSection.SetCheckedKeys(_selectedDestination.NodeFilter)
                _installSection.SetCheckedKeys(_selectedDestination.InstallationFilter)
                _instanceSection.SetCheckedKeys(_selectedDestination.InstanceFilter)
                _setSection.SetCheckedKeys(_selectedDestination.InstanceSetFilter)
                UpdateMatchLabel()

                ' Events
                For Each kvp In _eventChecks
                    kvp.Value.Checked = _selectedDestination.EnabledEventTypes.Contains(kvp.Key)
                Next

                ' Profile
                Dim profileIdx = 0
                For i = 0 To _profileCombo.Items.Count - 1
                    Dim p = TryCast(_profileCombo.Items(i), ProfileItem)
                    If p IsNot Nothing AndAlso p.ProfileId = _selectedDestination.VisibilityProfileId Then
                        profileIdx = i
                        Exit For
                    End If
                Next
                If _profileCombo.Items.Count > 0 Then _profileCombo.SelectedIndex = profileIdx
            Finally
                _suppressEvents = False
            End Try
        End Sub

        Private Sub ClearDetails()
            _suppressEvents = True
            Try
                _selectedDestination = Nothing
                _nameTextBox.Text = ""
                _webhookTextBox.Text = ""
                If _transportCombo.Items.Count > 0 Then _transportCombo.SelectedIndex = 0
                ApplyTransportVisibility()
                _guildCombo.Items.Clear()
                _channelCombo.Items.Clear()
                _enabledCheckBox.Checked = False
                _nodeSection.SetCheckedKeys(Nothing)
                _installSection.SetCheckedKeys(Nothing)
                _instanceSection.SetCheckedKeys(Nothing)
                _setSection.SetCheckedKeys(Nothing)
                UpdateMatchLabel()
                For Each cb In _eventChecks.Values
                    cb.Checked = False
                Next
                If _profileCombo.Items.Count > 0 Then _profileCombo.SelectedIndex = -1
            Finally
                _suppressEvents = False
            End Try
        End Sub

        ' ---- Transport, guild, channel UI swap ----

        ''' <summary>
        ''' Show/hide the transport-specific config controls based
        ''' on the current _transportCombo selection. Idempotent
        ''' — safe to call from both LoadDetailsFromSelection and
        ''' the OnTransportChanged handler.
        ''' </summary>
        Private Sub ApplyTransportVisibility()
            Dim isBot = IsBotTransportSelected()
            _webhookLabel.Visible = Not isBot
            _webhookTextBox.Visible = Not isBot
            _guildLabel.Visible = isBot
            _guildCombo.Visible = isBot
            _channelLabel.Visible = isBot
            _channelCombo.Visible = isBot
        End Sub

        Private Function IsBotTransportSelected() As Boolean
            Dim t = TryCast(_transportCombo.SelectedItem, TransportItem)
            Return t IsNot Nothing AndAlso
                   String.Equals(t.Kind, "DiscordBot", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>
        ''' Populate the Guild dropdown from the cached _botGuilds
        ''' list, then trigger the channel-list refresh and try to
        ''' select the previously-saved guild + channel IDs. If
        ''' the bot isn't connected (empty _botGuilds), shows a
        ''' single "(connect the bot first)" placeholder.
        ''' </summary>
        Private Sub PopulateGuildAndChannelCombos(selectedGuildId As String,
                                                    selectedChannelId As String)
            _guildCombo.Items.Clear()
            _channelCombo.Items.Clear()

            If _botGuilds Is Nothing OrElse _botGuilds.Count = 0 Then
                _guildCombo.Items.Add(New IdItemNotif("", "(connect the bot first)"))
                _guildCombo.SelectedIndex = 0
                _guildCombo.Enabled = False
                _channelCombo.Items.Add(New IdItemNotif("", "(connect the bot first)"))
                _channelCombo.SelectedIndex = 0
                _channelCombo.Enabled = False
                Return
            End If

            Dim selIdx = 0
            For i = 0 To _botGuilds.Count - 1
                Dim g = _botGuilds(i)
                _guildCombo.Items.Add(New IdItemNotif(g.GuildId, g.Name))
                If Not String.IsNullOrEmpty(selectedGuildId) AndAlso
                   String.Equals(g.GuildId, selectedGuildId, StringComparison.Ordinal) Then
                    selIdx = i
                End If
            Next
            _guildCombo.Enabled = True
            _guildCombo.SelectedIndex = selIdx
            ' SelectedIndexChanged doesn't fire on assigning the
            ' current value; force the channel-combo refresh.
            RefreshChannelComboForSelectedGuild()

            If Not String.IsNullOrEmpty(selectedChannelId) Then
                For i = 0 To _channelCombo.Items.Count - 1
                    Dim c = TryCast(_channelCombo.Items(i), IdItemNotif)
                    If c IsNot Nothing AndAlso
                       String.Equals(c.Id, selectedChannelId, StringComparison.Ordinal) Then
                        _channelCombo.SelectedIndex = i
                        Exit For
                    End If
                Next
            End If
        End Sub

        Private Sub RefreshChannelComboForSelectedGuild()
            _channelCombo.Items.Clear()
            Dim sel = TryCast(_guildCombo.SelectedItem, IdItemNotif)
            If sel Is Nothing OrElse String.IsNullOrEmpty(sel.Id) Then
                _channelCombo.Items.Add(New IdItemNotif("", "(no guild selected)"))
                _channelCombo.SelectedIndex = 0
                _channelCombo.Enabled = False
                Return
            End If

            Dim guild = _botGuilds.FirstOrDefault(Function(g) g.GuildId = sel.Id)
            If guild Is Nothing OrElse guild.Channels Is Nothing OrElse guild.Channels.Count = 0 Then
                _channelCombo.Items.Add(New IdItemNotif("", "(no postable channels)"))
                _channelCombo.SelectedIndex = 0
                _channelCombo.Enabled = False
                Return
            End If

            For Each ch In guild.Channels
                _channelCombo.Items.Add(New IdItemNotif(ch.ChannelId, "#" & ch.Name))
            Next
            _channelCombo.SelectedIndex = 0
            _channelCombo.Enabled = True
        End Sub

        ' ---- Instance selectors: per-installation scrolling listbox ----

        ' ---- Phase 5n scope accordion: populate + change wiring ----

        Private Sub PopulateScopeItems()
            Dim nodeItems As New List(Of KeyedItem)
            Dim seenNodes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim installItems As New List(Of KeyedItem)
            Dim instanceItems As New List(Of KeyedItem)
            Dim setItems As New List(Of KeyedItem)
            Dim seenSets As New HashSet(Of String)(StringComparer.Ordinal)

            For Each inst In _allInstallations
                Dim nodeName = If(inst.Node IsNot Nothing, inst.Node.DisplayName, "?")
                If Not String.IsNullOrEmpty(inst.NodeId) AndAlso seenNodes.Add(inst.NodeId) Then
                    nodeItems.Add(New KeyedItem(inst.NodeId, nodeName))
                End If
                installItems.Add(New KeyedItem(inst.InstallationId, $"{inst.DisplayName} ({nodeName})"))
                If inst.Instances IsNot Nothing Then
                    For Each ins In inst.Instances
                        instanceItems.Add(New KeyedItem(ins.InstanceId, $"{ins.DisplayName}  —  {inst.DisplayName}"))
                        If Not String.IsNullOrWhiteSpace(ins.InstanceSetTag) AndAlso seenSets.Add(ins.InstanceSetTag) Then
                            setItems.Add(New KeyedItem(ins.InstanceSetTag, ins.InstanceSetTag))
                        End If
                    Next
                End If
            Next

            _nodeSection.SetItems(nodeItems.OrderBy(Function(k) k.Display).ToList())
            _installSection.SetItems(installItems.OrderBy(Function(k) k.Display).ToList())
            _instanceSection.SetItems(instanceItems.OrderBy(Function(k) k.Display).ToList())
            _setSection.SetItems(setItems.OrderBy(Function(k) k.Display).ToList())
        End Sub

        Private Sub OnScopeNodeChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            SyncSet(_selectedDestination.NodeFilter, _nodeSection.GetCheckedKeys())
            UpdateMatchLabel()
        End Sub

        Private Sub OnScopeInstallationChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            SyncSet(_selectedDestination.InstallationFilter, _installSection.GetCheckedKeys())
            UpdateMatchLabel()
        End Sub

        Private Sub OnScopeInstanceChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            SyncSet(_selectedDestination.InstanceFilter, _instanceSection.GetCheckedKeys())
            UpdateMatchLabel()
        End Sub

        Private Sub OnScopeSetChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            SyncSet(_selectedDestination.InstanceSetFilter, _setSection.GetCheckedKeys())
            UpdateMatchLabel()
        End Sub

        ' Refills target from keys without replacing the HashSet, so the
        ' set's comparer (Ordinal for InstanceSetFilter, OrdinalIgnoreCase
        ' for the rest) is preserved.
        Private Shared Sub SyncSet(target As HashSet(Of String), keys As IEnumerable(Of String))
            target.Clear()
            If keys Is Nothing Then Return
            For Each k In keys
                target.Add(k)
            Next
        End Sub

        ' Live readout previewing the union-of-includes scope (the same
        ' predicate the send-time matcher adopts in Phase 5n-2).
        Private Sub UpdateMatchLabel()
            If _matchLabel Is Nothing Then Return
            Dim d = _selectedDestination
            If d Is Nothing Then
                _matchLabel.Text = ""
                Return
            End If
            Dim noFilter = (d.NodeFilter.Count + d.InstallationFilter.Count +
                            d.InstanceFilter.Count + d.InstanceSetFilter.Count) = 0
            Dim total = 0
            Dim inScope = 0
            For Each inst In _allInstallations
                If inst.Instances Is Nothing Then Continue For
                For Each ins In inst.Instances
                    total += 1
                    Dim hit As Boolean
                    If noFilter Then
                        hit = True
                    Else
                        hit = d.NodeFilter.Contains(inst.NodeId) OrElse
                              d.InstallationFilter.Contains(inst.InstallationId) OrElse
                              d.InstanceFilter.Contains(ins.InstanceId) OrElse
                              (Not String.IsNullOrEmpty(ins.InstanceSetTag) AndAlso
                               d.InstanceSetFilter.Contains(ins.InstanceSetTag))
                    End If
                    If hit Then inScope += 1
                Next
            Next
            If noFilter Then
                _matchLabel.Text = $"Matches all {total} instance(s)"
            Else
                _matchLabel.Text = $"Matches {inScope} of {total} instance(s)"
            End If
        End Sub

        Private Sub OnDetailsResize(sender As Object, e As EventArgs)
            If _lowerHost Is Nothing Then Return
            ' Size the lower column to the panel width; the scope box,
            ' events and visibility panels dock-fill it (no horizontal
            ' scroll), and the details panel scrolls the whole column.
            Dim w = _detailsPanel.ClientSize.Width - _lowerHost.Left - 20
            If w < 280 Then w = 280
            _lowerHost.Width = w
            If _scopeHint IsNot Nothing Then _scopeHint.Width = Math.Max(200, w - 24)
            RelayoutScope()
        End Sub

        ' Grows the scope box to fit its (expanded) sections, then sizes
        ' the lower column to the full stack so the details panel scrolls
        ' the lot — nothing inside the scope area scrolls on its own.
        Private Sub RelayoutScope()
            If _scopeHost Is Nothing OrElse _lowerHost Is Nothing Then Return
            Dim sh = 0
            For Each s In {_nodeSection, _installSection, _instanceSection, _setSection}
                sh += s.Height
            Next
            _scopeHost.Height = sh + 4
            Dim total = 0
            For Each c As Control In _lowerHost.Controls
                total += c.Height
            Next
            _lowerHost.Height = total + 4
        End Sub

        Private Sub OnScopeSectionExpanded(sender As Object, e As EventArgs)
            RelayoutScope()
        End Sub

        ' ---- Event handlers ----

        Private Sub OnNameChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            _selectedDestination.DisplayName = _nameTextBox.Text
            For Each item As ListViewItem In _destList.Items
                If item.Tag Is _selectedDestination Then
                    item.Text = _selectedDestination.DisplayName
                    Exit For
                End If
            Next
        End Sub

        Private Sub OnWebhookChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            _selectedDestination.WebhookUrl = _webhookTextBox.Text
        End Sub

        Private Sub OnTransportChanged(sender As Object, e As EventArgs)
            ' Visibility swap is unconditional (UI in sync with the
            ' selector), but mutating the edit model only happens
            ' if a destination is actually selected and we're not
            ' in the middle of LoadDetailsFromSelection.
            ApplyTransportVisibility()
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim t = TryCast(_transportCombo.SelectedItem, TransportItem)
            If t IsNot Nothing Then
                _selectedDestination.TransportKind = t.Kind
                ' Update the list view's Transport column for this row.
                For Each item As ListViewItem In _destList.Items
                    If item.Tag Is _selectedDestination Then
                        ' SubItems(0) is the row text itself; the
                        ' Transport column is SubItems(1).
                        If item.SubItems.Count >= 2 Then
                            item.SubItems(1).Text = TransportLabelFor(t.Kind)
                        End If
                        Exit For
                    End If
                Next
                ' If the user picks Bot but the bot isn't connected,
                ' the Guild dropdown will show the placeholder; no
                ' need for an additional warning here — Save will
                ' surface the missing GuildId/ChannelId.
                If IsBotTransportSelected() Then
                    If _guildCombo.Items.Count = 0 Then
                        PopulateGuildAndChannelCombos(_selectedDestination.GuildId,
                                                      _selectedDestination.ChannelId)
                    End If
                    ' Sync the currently-displayed combo selections
                    ' back to the edit model. Without this, a user
                    ' who switches transport to Bot and accepts the
                    ' default guild/channel — without re-clicking
                    ' them — leaves the model with empty IDs (the
                    ' SelectedIndex assignments inside
                    ' PopulateGuildAndChannelCombos run under
                    ' _suppressEvents, so the change handlers never
                    ' fire). Save validation then rejects what the
                    ' form visibly shows as a fully-populated
                    ' destination. Treating the displayed defaults
                    ' as implicit consent fixes that.
                    Dim guildSel = TryCast(_guildCombo.SelectedItem, IdItemNotif)
                    If guildSel IsNot Nothing Then
                        _selectedDestination.GuildId = guildSel.Id
                    End If
                    Dim channelSel = TryCast(_channelCombo.SelectedItem, IdItemNotif)
                    If channelSel IsNot Nothing Then
                        _selectedDestination.ChannelId = channelSel.Id
                    End If
                End If
            End If
        End Sub

        Private Sub OnGuildChangedNotif(sender As Object, e As EventArgs)
            ' Channel list always rebuilds on guild change so the
            ' user sees only postable channels in the new guild.
            ' (Visible regardless of _suppressEvents — the user's
            ' UI must stay consistent with their selection.)
            RefreshChannelComboForSelectedGuild()
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim sel = TryCast(_guildCombo.SelectedItem, IdItemNotif)
            _selectedDestination.GuildId = If(sel IsNot Nothing, sel.Id, "")
            ' Sync the channel selection that
            ' RefreshChannelComboForSelectedGuild just defaulted to
            ' (first item in the new guild). SelectedIndex = 0 inside
            ' that helper silently picks the first channel without
            ' firing OnChannelChangedNotif, so without this sync the
            ' model would carry an empty ChannelId despite the form
            ' visibly showing one. Same fix pattern as OnTransportChanged.
            Dim channelSel = TryCast(_channelCombo.SelectedItem, IdItemNotif)
            _selectedDestination.ChannelId = If(channelSel IsNot Nothing, channelSel.Id, "")
        End Sub

        Private Sub OnChannelChangedNotif(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim sel = TryCast(_channelCombo.SelectedItem, IdItemNotif)
            _selectedDestination.ChannelId = If(sel IsNot Nothing, sel.Id, "")
        End Sub

        Private Sub OnEnabledCheckBoxChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            _selectedDestination.Enabled = _enabledCheckBox.Checked
            For Each item As ListViewItem In _destList.Items
                If item.Tag Is _selectedDestination Then
                    item.ForeColor = If(_selectedDestination.Enabled, Color.Black, Color.Gray)
                    Exit For
                End If
            Next
        End Sub


        Private Sub OnEventCheckChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim cb = DirectCast(sender, CheckBox)
            Dim evt = DirectCast(cb.Tag, NotificationEventType)
            If cb.Checked Then
                _selectedDestination.EnabledEventTypes.Add(evt)
            Else
                _selectedDestination.EnabledEventTypes.Remove(evt)
            End If
        End Sub

        Private Sub OnProfileChanged(sender As Object, e As EventArgs)
            If _suppressEvents OrElse _selectedDestination Is Nothing Then Return
            Dim p = TryCast(_profileCombo.SelectedItem, ProfileItem)
            If p IsNot Nothing Then _selectedDestination.VisibilityProfileId = p.ProfileId
        End Sub

        Private Sub OnAddClicked(sender As Object, e As EventArgs)
            Dim d As New DestinationEdit() With {
                .DestinationId = Guid.NewGuid().ToString("N"),
                .DisplayName = "New Destination",
                .Enabled = True,
                .VisibilityProfileId = "public",
                .TransportKind = "DiscordWebhook"
            }
            _destinations.Add(d)
            RefreshDestinationList()
            ' Select the new one
            For Each item As ListViewItem In _destList.Items
                If item.Tag Is d Then
                    item.Selected = True
                    _nameTextBox.Focus()
                    _nameTextBox.SelectAll()
                    Exit For
                End If
            Next
        End Sub

        Private Sub OnRemoveClicked(sender As Object, e As EventArgs)
            If _selectedDestination Is Nothing Then Return
            Dim result = MessageBox.Show(
                $"Remove destination '{_selectedDestination.DisplayName}'?",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If result <> DialogResult.Yes Then Return
            _destinations.Remove(_selectedDestination)
            _selectedDestination = Nothing
            RefreshDestinationList()
            ClearDetails()
        End Sub

        Private Async Sub OnTestClicked(sender As Object, e As EventArgs)
            If _selectedDestination Is Nothing Then Return

            _testButton.Enabled = False
            _testButton.Text = "Sending..."
            Try
                Dim err As String = Nothing
                If String.Equals(_selectedDestination.TransportKind, "DiscordBot",
                                  StringComparison.OrdinalIgnoreCase) Then
                    ' Bot transport — needs guild + channel.
                    If String.IsNullOrWhiteSpace(_selectedDestination.GuildId) OrElse
                       String.IsNullOrWhiteSpace(_selectedDestination.ChannelId) Then
                        MessageBox.Show("Pick a guild and channel first.", "Test",
                                        MessageBoxButtons.OK, MessageBoxIcon.Information)
                        Return
                    End If
                    Dim botPlugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
                    If botPlugin Is Nothing Then
                        MessageBox.Show("Discord bot plugin not registered.", "Test",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error)
                        Return
                    End If
                    err = Await botPlugin.SendDestinationTestAsync(
                        _selectedDestination.GuildId,
                        _selectedDestination.ChannelId,
                        _selectedDestination.DisplayName,
                        CancellationToken.None)
                Else
                    ' Webhook transport — needs URL.
                    If String.IsNullOrWhiteSpace(_selectedDestination.WebhookUrl) Then
                        MessageBox.Show("Enter a webhook URL first.", "Test",
                                        MessageBoxButtons.OK, MessageBoxIcon.Information)
                        Return
                    End If
                    Dim plugin = ManagerProgram.Services.GetService(Of DiscordWebhookPlugin)()
                    If plugin Is Nothing Then
                        MessageBox.Show("Discord webhook plugin not registered.", "Test",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error)
                        Return
                    End If
                    err = Await plugin.SendTestAsync(
                        _selectedDestination.WebhookUrl,
                        _selectedDestination.DisplayName,
                        CancellationToken.None)
                End If

                If String.IsNullOrEmpty(err) Then
                    MessageBox.Show("Test message sent successfully.", "Test",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information)
                Else
                    MessageBox.Show($"Test failed: {err}", "Test",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error)
                End If
            Finally
                _testButton.Text = "Test"
                _testButton.Enabled = True
            End Try
        End Sub

        Private Sub OnManageProfilesClicked(sender As Object, e As EventArgs)
            Using dlg As New VisibilityProfileEditorForm()
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    ' Reload profiles into combo
                    ReloadProfilesAsync()
                End If
            End Using
        End Sub

        Private Async Sub ReloadProfilesAsync()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim profiles = Await db.VisibilityProfiles.ToListAsync()
                    Dim currentId = If(_selectedDestination IsNot Nothing,
                                        _selectedDestination.VisibilityProfileId, "")
                    _suppressEvents = True
                    _profileCombo.Items.Clear()
                    Dim idx = 0
                    Dim selectIdx = 0
                    For Each p In profiles
                        _profileCombo.Items.Add(New ProfileItem(p.ProfileId, p.DisplayName))
                        If p.ProfileId = currentId Then selectIdx = idx
                        idx += 1
                    Next
                    If _profileCombo.Items.Count > 0 Then _profileCombo.SelectedIndex = selectIdx
                    _suppressEvents = False
                End Using
            Catch ex As Exception
                MessageBox.Show($"Failed to reload profiles: {ex.Message}")
            End Try
        End Sub

        Private Sub OnCustomizeTemplatesClicked(sender As Object, e As EventArgs)
            If _selectedDestination Is Nothing Then
                MessageBox.Show("Select a destination first.")
                Return
            End If
            Using dlg As New TemplateEditorForm(_selectedDestination.TemplateOverrides)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    _selectedDestination.TemplateOverrides = dlg.ResultTemplates
                End If
            End Using
        End Sub

        ' ---- Save ----

        Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
            ' Validation — every bot-transport destination needs a
            ' resolvable guild + channel before save. Webhook URLs
            ' are validated implicitly (the Test button surfaces
            ' bad URLs; an empty URL just means "won't dispatch"
            ' which is acceptable).
            For Each d In _destinations
                If String.Equals(d.TransportKind, "DiscordBot",
                                  StringComparison.OrdinalIgnoreCase) Then
                    If String.IsNullOrWhiteSpace(d.GuildId) OrElse
                       String.IsNullOrWhiteSpace(d.ChannelId) Then
                        MessageBox.Show(
                            $"Bot destination '{d.DisplayName}' is missing a guild or channel.",
                            "Save blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                        Return
                    End If
                End If
            Next

            _saveButton.Enabled = False
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' Load the FULL set of existing destination IDs
                    ' (across both transports) so deletions sweep
                    ' rows the user removed regardless of transport.
                    Dim existingIds = Await db.NotificationDestinations.
                        Select(Function(d) d.DestinationId).ToListAsync()

                    Dim editIds = _destinations.Select(Function(d) d.DestinationId).ToHashSet()

                    ' Deletions
                    For Each id In existingIds
                        If Not editIds.Contains(id) Then
                            Dim ent = Await db.NotificationDestinations.FindAsync(id)
                            If ent IsNot Nothing Then db.NotificationDestinations.Remove(ent)
                        End If
                    Next

                    ' Upserts
                    For Each d In _destinations
                        Dim ent = Await db.NotificationDestinations.FindAsync(d.DestinationId)
                        If ent Is Nothing Then
                            ent = New NotificationDestinationEntity() With {
                                .DestinationId = d.DestinationId,
                                .CreatedUtc = DateTime.UtcNow
                            }
                            db.NotificationDestinations.Add(ent)
                        End If
                        ent.DisplayName = d.DisplayName
                        ent.Enabled = d.Enabled
                        ' Persist the edited transport — may have
                        ' changed since load (user toggled the
                        ' Transport dropdown).
                        ent.TransportKind = If(d.TransportKind, "DiscordWebhook")

                        ' Build TransportConfigJson per transport.
                        ' Webhook: {"WebhookUrl":"…"}
                        ' Bot:     {"GuildId":"…","ChannelId":"…"}
                        ' Other transport-irrelevant fields are
                        ' dropped on transport switch — no point
                        ' carrying a stale URL on a bot row, and
                        ' the Cache loaders ignore unknown keys.
                        If String.Equals(ent.TransportKind, "DiscordBot",
                                          StringComparison.OrdinalIgnoreCase) Then
                            ent.TransportConfigJson = JsonSerializer.Serialize(
                                New Dictionary(Of String, String) From {
                                    {"GuildId", If(d.GuildId, "")},
                                    {"ChannelId", If(d.ChannelId, "")}
                                })
                        Else
                            ent.TransportConfigJson = JsonSerializer.Serialize(
                                New Dictionary(Of String, String) From {
                                    {"WebhookUrl", If(d.WebhookUrl, "")}
                                })
                        End If

                        ent.EnabledEventTypesJson = JsonSerializer.Serialize(
                            d.EnabledEventTypes.Select(Function(x) x.ToString()).ToList())
                        ent.InstallationFilterJson = JsonSerializer.Serialize(d.InstallationFilter.ToList())
                        ent.InstanceFilterJson = JsonSerializer.Serialize(d.InstanceFilter.ToList())
                        ent.NodeFilterJson = JsonSerializer.Serialize(d.NodeFilter.ToList())
                        ent.InstanceSetFilterJson = JsonSerializer.Serialize(d.InstanceSetFilter.ToList())
                        ent.VisibilityProfileId = d.VisibilityProfileId
                        If d.TemplateOverrides Is Nothing OrElse d.TemplateOverrides.Count = 0 Then
                            ent.TemplateOverridesJson = Nothing
                        Else
                            Dim asStrings = d.TemplateOverrides.ToDictionary(
                                Function(kv) kv.Key.ToString(),
                                Function(kv) kv.Value)
                            ent.TemplateOverridesJson = JsonSerializer.Serialize(asStrings)
                        End If
                        ent.UpdatedUtc = DateTime.UtcNow
                    Next

                    Await db.SaveChangesAsync()
                End Using

                ' Reload BOTH plugin caches — destinations may have
                ' been added/removed/transport-switched, and each
                ' plugin only owns its own TransportKind subset.
                Dim webhookPlugin = ManagerProgram.Services.GetService(Of DiscordWebhookPlugin)()
                If webhookPlugin IsNot Nothing Then Await webhookPlugin.RefreshConfigAsync()
                Dim botPlugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
                If botPlugin IsNot Nothing Then Await botPlugin.RefreshDestinationsConfigAsync()

                Me.DialogResult = DialogResult.OK
                Me.Close()
            Catch ex As Exception
                MessageBox.Show($"Save failed: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error)
            Finally
                _saveButton.Enabled = True
            End Try
        End Sub

        ' ---- Internal edit model + combo item holders ----

        Private Class DestinationEdit
            Public Property DestinationId As String
            Public Property DisplayName As String
            Public Property Enabled As Boolean
            ''' <summary>
            ''' Canonical TransportKind string — "DiscordWebhook" or
            ''' "DiscordBot". Defaults to DiscordWebhook for new
            ''' destinations and for legacy rows whose entity
            ''' didn't carry the value.
            ''' </summary>
            Public Property TransportKind As String = "DiscordWebhook"
            Public Property WebhookUrl As String
            ''' <summary>Bot transport: target Discord guild ID.</summary>
            Public Property GuildId As String
            ''' <summary>Bot transport: target Discord channel ID.</summary>
            Public Property ChannelId As String
            Public Property VisibilityProfileId As String
            Public Property EnabledEventTypes As New HashSet(Of NotificationEventType)
            Public Property InstallationFilter As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Public Property InstanceFilter As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            ' Phase 5n scope dimensions. NodeFilter holds node IDs;
            ' InstanceSetFilter holds InstanceSetTag values and is
            ' case-sensitive (Ordinal) to match RuleScope.InstanceSet.
            Public Property NodeFilter As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Public Property InstanceSetFilter As New HashSet(Of String)(StringComparer.Ordinal)
            Public Property TemplateOverrides As Dictionary(Of NotificationEventType, String)

            Public Shared Function FromEntity(e As NotificationDestinationEntity) As DestinationEdit
                Dim edit As New DestinationEdit() With {
                    .DestinationId = e.DestinationId,
                    .DisplayName = e.DisplayName,
                    .Enabled = e.Enabled,
                    .VisibilityProfileId = e.VisibilityProfileId,
                    .TransportKind = If(String.IsNullOrEmpty(e.TransportKind), "DiscordWebhook", e.TransportKind)
                }
                ' Parse TransportConfigJson per transport. Tolerant
                ' of unexpected shapes — missing keys leave the
                ' relevant field empty and let the form's
                ' validation surface them on save.
                If Not String.IsNullOrEmpty(e.TransportConfigJson) Then
                    Try
                        Dim d = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(e.TransportConfigJson)
                        If d IsNot Nothing Then
                            Dim webhook As String = Nothing
                            d.TryGetValue("WebhookUrl", webhook)
                            edit.WebhookUrl = webhook

                            Dim guildId As String = Nothing
                            d.TryGetValue("GuildId", guildId)
                            edit.GuildId = guildId

                            Dim channelId As String = Nothing
                            d.TryGetValue("ChannelId", channelId)
                            edit.ChannelId = channelId
                        End If
                    Catch
                    End Try
                End If
                edit.EnabledEventTypes = ParseEnumSet(e.EnabledEventTypesJson)
                edit.InstallationFilter = ParseStringSet(e.InstallationFilterJson)
                edit.InstanceFilter = ParseStringSet(e.InstanceFilterJson)
                edit.NodeFilter = ParseStringSet(e.NodeFilterJson)
                edit.InstanceSetFilter = ParseStringSet(e.InstanceSetFilterJson, StringComparer.Ordinal)
                edit.TemplateOverrides = ParseTemplateOverrides(e.TemplateOverridesJson)
                Return edit
            End Function

            Private Shared Function ParseEnumSet(json As String) As HashSet(Of NotificationEventType)
                Dim result As New HashSet(Of NotificationEventType)
                If String.IsNullOrEmpty(json) Then Return result
                Try
                    Dim list = JsonSerializer.Deserialize(Of List(Of String))(json)
                    If list IsNot Nothing Then
                        For Each n In list
                            Dim v As NotificationEventType
                            If [Enum].TryParse(n, True, v) Then result.Add(v)
                        Next
                    End If
                Catch
                End Try
                Return result
            End Function

            Private Shared Function ParseStringSet(json As String,
                                                   Optional comparer As IEqualityComparer(Of String) = Nothing) As HashSet(Of String)
                Dim cmp As IEqualityComparer(Of String) =
                    If(comparer, DirectCast(StringComparer.OrdinalIgnoreCase, IEqualityComparer(Of String)))
                Dim result As New HashSet(Of String)(cmp)
                If String.IsNullOrEmpty(json) Then Return result
                Try
                    Dim list = JsonSerializer.Deserialize(Of List(Of String))(json)
                    If list IsNot Nothing Then
                        For Each v In list
                            If Not String.IsNullOrWhiteSpace(v) Then result.Add(v)
                        Next
                    End If
                Catch
                End Try
                Return result
            End Function

            Private Shared Function ParseTemplateOverrides(json As String) As Dictionary(Of NotificationEventType, String)
                Dim result As New Dictionary(Of NotificationEventType, String)
                If String.IsNullOrEmpty(json) Then Return result
                Try
                    Dim raw = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
                    If raw IsNot Nothing Then
                        For Each kvp In raw
                            Dim v As NotificationEventType
                            If [Enum].TryParse(kvp.Key, True, v) Then result(v) = kvp.Value
                        Next
                    End If
                Catch
                End Try
                Return result
            End Function
        End Class

        Private Class InstallationItem
            Public ReadOnly InstallationId As String
            Public ReadOnly Label As String
            Public Sub New(id As String, label As String)
                Me.InstallationId = id
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        Private Class InstanceItem
            Public ReadOnly InstanceId As String
            Public ReadOnly Label As String
            Public Sub New(id As String, label As String)
                Me.InstanceId = id
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        Private Class ProfileItem
            Public ReadOnly ProfileId As String
            Public ReadOnly Label As String
            Public Sub New(id As String, label As String)
                Me.ProfileId = id
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        ''' <summary>
        ''' Combo item for the Transport selector. Carries the
        ''' canonical TransportKind string ("DiscordWebhook" /
        ''' "DiscordBot") plus the human-friendly display label.
        ''' </summary>
        Private Class TransportItem
            Public ReadOnly Kind As String
            Public ReadOnly Label As String
            Public Sub New(kind As String, label As String)
                Me.Kind = kind
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

        ''' <summary>
        ''' Generic ID/Display combo item used by the Guild and
        ''' Channel dropdowns. Suffixed "Notif" because there's
        ''' an unrelated IdItem private class in
        ''' DiscordPanelEditorForm; same shape, separate file
        ''' for accessibility reasons.
        ''' </summary>
        Private Class IdItemNotif
            Public ReadOnly Id As String
            Public ReadOnly Display As String
            Public Sub New(id As String, display As String)
                Me.Id = id
                Me.Display = display
            End Sub
            Public Overrides Function ToString() As String
                Return Display
            End Function
        End Class

    End Class

    ' ============================================================
    '  CollapsibleCheckSection — one accordion section for the
    '  notification scope editor: a clickable header (glyph + title
    '  + live "N of M selected" summary) over a CheckedListBox.
    '  Collapsed shows only the header; the summary keeps the
    '  selection visible while collapsed. Items are KeyedItem
    '  (Key + Display); membership is keyed on Key.
    ' ============================================================
    Friend Class CollapsibleCheckSection
        Inherits Panel

        Private Const HeaderH As Integer = 24

        Private ReadOnly _title As String
        Private ReadOnly _header As Label
        Private ReadOnly _list As CheckedListBox
        Private _expanded As Boolean
        Private _suppress As Boolean

        Public Event CheckedChanged As EventHandler
        Public Event ExpandedChanged As EventHandler

        Public Sub New(title As String)
            _title = title
            Me.Dock = DockStyle.Top
            Me.Height = HeaderH

            ' List added first, header second, so the header docks above
            ' the list. Both Dock.Top, so width tracks the section (and
            ' the section tracks its scrolling host) — no fixed width.
            _list = New CheckedListBox() With {
                .Dock = DockStyle.Top,
                .Height = 22,
                .CheckOnClick = True,
                .IntegralHeight = False,
                .Visible = False
            }
            AddHandler _list.ItemCheck, AddressOf OnItemCheck
            AddHandler _list.MouseWheel, AddressOf OnListMouseWheel
            Me.Controls.Add(_list)

            _header = New Label() With {
                .Dock = DockStyle.Top,
                .Height = HeaderH,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .TextAlign = ContentAlignment.MiddleLeft,
                .Cursor = Cursors.Hand,
                .BackColor = Color.FromArgb(238, 238, 244)
            }
            AddHandler _header.Click, AddressOf OnHeaderClick
            Me.Controls.Add(_header)

            UpdateHeader()
        End Sub

        Private Sub OnHeaderClick(sender As Object, e As EventArgs)
            SetExpanded(Not _expanded)
        End Sub

        ' The grown list never needs to scroll itself, so forward the
        ' wheel to the nearest auto-scrolling parent. Otherwise the list
        ' swallows the wheel and the panel won't scroll while the pointer
        ' is over a section.
        Private Sub OnListMouseWheel(sender As Object, e As MouseEventArgs)
            Dim h = TryCast(e, HandledMouseEventArgs)
            If h IsNot Nothing Then h.Handled = True
            Dim p As Control = Me.Parent
            While p IsNot Nothing
                Dim sc = TryCast(p, ScrollableControl)
                If sc IsNot Nothing AndAlso sc.AutoScroll Then
                    Dim cur = -sc.AutoScrollPosition.Y
                    sc.AutoScrollPosition = New Point(0, cur - e.Delta)
                    Return
                End If
                p = p.Parent
            End While
        End Sub

        Public Sub SetExpanded(value As Boolean)
            _expanded = value
            _list.Visible = value
            Me.Height = If(value, HeaderH + _list.Height, HeaderH)
            UpdateHeader()
            RaiseEvent ExpandedChanged(Me, EventArgs.Empty)
        End Sub

        Public Sub SetItems(items As IEnumerable(Of KeyedItem))
            _suppress = True
            _list.Items.Clear()
            If items IsNot Nothing Then
                For Each it In items
                    _list.Items.Add(it)
                Next
            End If
            _suppress = False
            ResizeListToFit()
            UpdateHeader()
        End Sub

        ' Sizes the list to show every row, so an expanded section grows
        ' to fit its items instead of scrolling internally.
        Private Sub ResizeListToFit()
            ' Use the control's own item height (font/DPI-exact), take the
            ' larger of that and a measured fallback, and add a buffer for
            ' the list border so rows never trigger an inner scrollbar.
            Dim rowH = _list.ItemHeight
            Dim measured = TextRenderer.MeasureText("Wg", _list.Font).Height + 2
            If rowH < measured Then rowH = measured
            Dim n = Math.Max(1, _list.Items.Count)
            _list.Height = n * rowH + 8
            If _expanded Then Me.Height = HeaderH + _list.Height
        End Sub

        ' Checks the rows whose Key is in keys, using keys' own equality
        ' semantics (pass the backing HashSet so its comparer applies).
        ' Nothing = clear all.
        Public Sub SetCheckedKeys(keys As ICollection(Of String))
            _suppress = True
            For i = 0 To _list.Items.Count - 1
                Dim it = TryCast(_list.Items(i), KeyedItem)
                Dim on_ = it IsNot Nothing AndAlso keys IsNot Nothing AndAlso keys.Contains(it.Key)
                _list.SetItemChecked(i, on_)
            Next
            _suppress = False
            UpdateHeader()
        End Sub

        Public Function GetCheckedKeys() As List(Of String)
            Dim result As New List(Of String)
            For Each o In _list.CheckedItems
                Dim it = TryCast(o, KeyedItem)
                If it IsNot Nothing Then result.Add(it.Key)
            Next
            Return result
        End Function

        Private Sub OnItemCheck(sender As Object, e As ItemCheckEventArgs)
            If _suppress Then Return
            ' ItemCheck fires before the row's state flips; defer so the
            ' summary and GetCheckedKeys() see the new state.
            Me.BeginInvoke(New MethodInvoker(AddressOf RaiseChanged))
        End Sub

        Private Sub RaiseChanged()
            UpdateHeader()
            RaiseEvent CheckedChanged(Me, EventArgs.Empty)
        End Sub

        Private Sub UpdateHeader()
            Dim glyph = If(_expanded, "▼", "▶")
            Dim n = _list.CheckedItems.Count
            Dim total = _list.Items.Count
            Dim summary = If(n = 0, "none", n & " of " & total & " selected")
            _header.Text = glyph & "  " & _title & "   —   " & summary
        End Sub

    End Class

    Friend Class KeyedItem
        Public ReadOnly Key As String
        Public ReadOnly Display As String
        Public Sub New(key As String, display As String)
            Me.Key = key
            Me.Display = display
        End Sub
        Public Overrides Function ToString() As String
            Return Display
        End Function
    End Class

End Namespace