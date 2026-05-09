Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data

' ============================================================
'  DiscordBotForm — top-level configuration window for the
'  Discord bot plugin (Phase 5d-1).
'
'  Two independent sections in one window:
'    1. Bot Setup  — display name, encrypted token, enabled
'                    toggle, "Test connection" button.
'    2. Panels     — list of configured DiscordPanel rows with
'                    Add / Edit / Remove. Add/Edit open
'                    DiscordPanelEditorForm modally.
'
'  Bot Setup writes to a single "default" DiscordBotConfigEntity
'  row; the schema reserves the column for future multi-identity
'  support but v1 only ever has one row. Panel changes write
'  individual DiscordPanelEntity rows.
'
'  After every save (bot config or panels), the form calls
'  DiscordBotPlugin.ReloadConfigAsync (for token/enable changes,
'  which require a reconnect) or RequestRefreshAllPanels (for
'  panel changes, which don't). The plugin handles the rest.
'
'  Token UX: never round-trip the decrypted token through the
'  form. On open, if a token is configured, the textbox shows a
'  placeholder "(token saved — leave blank to keep)" and is
'  empty. Save: if the textbox is empty, leave the encrypted
'  bytes alone; if non-empty, encrypt the new value.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DiscordBotForm
        Inherits Form

        Private Const DefaultConfigId As String = "default"
        Private Const TokenPlaceholder As String = "(token saved — leave blank to keep)"

        ' ---- Bot Setup section ----
        Private _displayNameTextBox As TextBox
        Private _tokenTextBox As TextBox
        Private _showTokenButton As Button
        Private _enabledCheckBox As CheckBox
        Private _testButton As Button
        Private _saveBotButton As Button
        Private _roleMappingsButton As Button
        Private _statusLabel As Label

        ' ---- Panels section ----
        Private _panelsList As ListView
        Private _addPanelButton As Button
        Private _editPanelButton As Button
        Private _removePanelButton As Button

        ' ---- Bottom ----
        Private _closeButton As Button

        ' Tracks whether the displayed token field carries the
        ' "(saved)" placeholder vs. user-typed content. Lets us
        ' decide on save whether to re-encrypt or leave the
        ' stored bytes alone.
        Private _tokenIsPlaceholder As Boolean = False
        Private _hasStoredToken As Boolean = False

        ' Phase 5d-5 item 5 — polls the plugin's connection state
        ' once a second so the status label tracks reality without
        ' the operator having to close + reopen the form. Also
        ' drives the live uptime counter on the second line of the
        ' label. Disposed in FormClosed so the timer doesn't keep
        ' the form rooted after close.
        '
        ' Fully-qualified type: the form imports both
        ' System.Threading and System.Windows.Forms, so the bare
        ' name 'Timer' is ambiguous (BC30560). The Forms Timer
        ' is what we want — it ticks on the UI thread, so the
        ' Tick handler can touch _statusLabel safely without an
        ' Invoke marshal.
        Private _pollTimer As System.Windows.Forms.Timer

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            LoadFromDb()
            UpdateStatusLabel()

            _pollTimer = New System.Windows.Forms.Timer With {.Interval = 1000}
            AddHandler _pollTimer.Tick, Sub(s, e) UpdateStatusLabel()
            AddHandler Me.FormClosed, AddressOf OnFormClosedDisposeTimer
            _pollTimer.Start()
        End Sub

        Private Sub OnFormClosedDisposeTimer(sender As Object, e As FormClosedEventArgs)
            ' Stop+Dispose so the WinForms timer's underlying
            ' window-message hook is released; otherwise the form
            ' stays alive in the message loop until GC catches up.
            Try
                If _pollTimer IsNot Nothing Then
                    _pollTimer.Stop()
                    _pollTimer.Dispose()
                    _pollTimer = Nothing
                End If
            Catch
            End Try
        End Sub

        ' ============================================================
        '  Layout
        ' ============================================================

        Private Sub InitializeControls()
            Me.Text = "Discord Bot"
            Me.Size = New Size(1000, 620)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.MinimumSize = New Size(880, 540)

            Dim root As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 3,
                .Padding = New Padding(10)
            }
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 240))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))

            root.Controls.Add(BuildBotSetupGroup(), 0, 0)
            root.Controls.Add(BuildPanelsGroup(), 0, 1)
            root.Controls.Add(BuildButtonRow(), 0, 2)

            Me.Controls.Add(root)
        End Sub

        Private Function BuildBotSetupGroup() As GroupBox
            Dim grp As New GroupBox With {
                .Text = "Bot Setup",
                .Dock = DockStyle.Fill,
                .Padding = New Padding(10)
            }

            Dim layout As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 3,
                .RowCount = 6
            }
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 130))
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 100))
            For i = 0 To 4
                layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 32))
            Next
            layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            ' Display name
            layout.Controls.Add(MakeLabel("Display name:"), 0, 0)
            _displayNameTextBox = New TextBox With {
                .Dock = DockStyle.Fill,
                .Text = "PowerGSM Bot"
            }
            layout.SetColumnSpan(_displayNameTextBox, 2)
            layout.Controls.Add(_displayNameTextBox, 1, 0)

            ' Token + Show button
            layout.Controls.Add(MakeLabel("Bot token:"), 0, 1)
            _tokenTextBox = New TextBox With {
                .Dock = DockStyle.Fill,
                .UseSystemPasswordChar = True
            }
            AddHandler _tokenTextBox.Enter, AddressOf OnTokenEnter
            AddHandler _tokenTextBox.TextChanged, AddressOf OnTokenChanged
            layout.Controls.Add(_tokenTextBox, 1, 1)
            _showTokenButton = New Button With {
                .Text = "Show",
                .Dock = DockStyle.Fill
            }
            AddHandler _showTokenButton.Click, AddressOf OnToggleShowToken
            layout.Controls.Add(_showTokenButton, 2, 1)

            ' Enabled
            layout.Controls.Add(MakeLabel("Enabled:"), 0, 2)
            _enabledCheckBox = New CheckBox With {
                .Text = "Connect to Discord on Manager startup",
                .Dock = DockStyle.Fill,
                .Checked = True
            }
            layout.SetColumnSpan(_enabledCheckBox, 2)
            layout.Controls.Add(_enabledCheckBox, 1, 2)

            ' Buttons
            Dim buttonRow As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.LeftToRight
            }
            ' Margin pattern for a horizontal button row in a
            ' FlowLayoutPanel: every button gets explicit zero
            ' top/bottom margins so they share a baseline (the
            ' FlowLayoutPanel default is Padding(3) which would
            ' shift any button using the default down a few px,
            ' breaking alignment with its neighbours). Right
            ' margins create the inter-button gaps; the last
            ' button has zero right margin since nothing follows it.
            _testButton = New Button With {.Text = "Test Connection", .Width = 130, .Margin = New Padding(0, 0, 8, 0)}
            _saveBotButton = New Button With {.Text = "Save Bot Config", .Width = 130, .Margin = New Padding(0, 0, 8, 0)}
            AddHandler _testButton.Click, AddressOf OnTestConnection
            AddHandler _saveBotButton.Click, AddressOf OnSaveBotConfig
            buttonRow.Controls.Add(_testButton)
            buttonRow.Controls.Add(_saveBotButton)
            ' Role Mappings... opens the per-guild role-to-permission
            ' mapping editor (Phase 5d-3). Co-located with bot setup
            ' rather than panels because the mapping is a property
            ' of the bot identity / guilds it's in, not of any
            ' particular panel.
            _roleMappingsButton = New Button With {.Text = "Role Mappings...", .Width = 130, .Margin = New Padding(0)}
            AddHandler _roleMappingsButton.Click, AddressOf OnRoleMappings
            buttonRow.Controls.Add(_roleMappingsButton)
            layout.SetColumnSpan(buttonRow, 2)
            layout.Controls.Add(buttonRow, 1, 3)

            ' Status label
            _statusLabel = New Label With {
                .Dock = DockStyle.Fill,
                .ForeColor = SystemColors.GrayText,
                .TextAlign = ContentAlignment.TopLeft,
                .AutoSize = False
            }
            layout.SetColumnSpan(_statusLabel, 3)
            layout.Controls.Add(_statusLabel, 0, 5)

            grp.Controls.Add(layout)
            Return grp
        End Function

        Private Function BuildPanelsGroup() As GroupBox
            Dim grp As New GroupBox With {
                .Text = "Panels",
                .Dock = DockStyle.Fill,
                .Padding = New Padding(10)
            }

            Dim layout As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1
            }
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 120))
            layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            _panelsList = New ListView With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .HideSelection = False,
                .MultiSelect = False
            }
            _panelsList.Columns.Add("Name", 280)
            _panelsList.Columns.Add("Guild", 180)
            _panelsList.Columns.Add("Channel", 150)
            _panelsList.Columns.Add("Scope", 220)
            AddHandler _panelsList.DoubleClick, AddressOf OnEditPanel
            AddHandler _panelsList.SelectedIndexChanged, AddressOf OnPanelsSelectionChanged
            layout.Controls.Add(_panelsList, 0, 0)

            Dim buttonStack As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.TopDown,
                .Padding = New Padding(8, 0, 0, 0)
            }
            _addPanelButton = New Button With {.Text = "Add...", .Width = 100, .Margin = New Padding(0, 0, 0, 6)}
            _editPanelButton = New Button With {.Text = "Edit...", .Width = 100, .Margin = New Padding(0, 0, 0, 6), .Enabled = False}
            _removePanelButton = New Button With {.Text = "Remove", .Width = 100, .Margin = New Padding(0, 0, 0, 6), .Enabled = False}
            AddHandler _addPanelButton.Click, AddressOf OnAddPanel
            AddHandler _editPanelButton.Click, AddressOf OnEditPanel
            AddHandler _removePanelButton.Click, AddressOf OnRemovePanel
            buttonStack.Controls.Add(_addPanelButton)
            buttonStack.Controls.Add(_editPanelButton)
            buttonStack.Controls.Add(_removePanelButton)
            layout.Controls.Add(buttonStack, 1, 0)

            grp.Controls.Add(layout)
            Return grp
        End Function

        Private Function BuildButtonRow() As FlowLayoutPanel
            Dim row As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.RightToLeft,
                .Padding = New Padding(0, 8, 0, 0)
            }
            _closeButton = New Button With {.Text = "Close", .Width = 100}
            AddHandler _closeButton.Click, Sub(s, e) Me.Close()
            row.Controls.Add(_closeButton)
            Return row
        End Function

        Private Function MakeLabel(text As String) As Label
            Return New Label With {
                .Text = text,
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }
        End Function

        ' ============================================================
        '  Loading from DB
        ' ============================================================

        Private Sub LoadFromDb()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                    ' Bot config — single "default" row.
                    Dim cfg = db.DiscordBotConfigs.Find(DefaultConfigId)
                    If cfg IsNot Nothing Then
                        _displayNameTextBox.Text = If(cfg.DisplayName, "PowerGSM Bot")
                        _enabledCheckBox.Checked = cfg.Enabled
                        _hasStoredToken = (cfg.EncryptedToken IsNot Nothing AndAlso
                                           cfg.EncryptedToken.Length > 0)
                    End If
                    If _hasStoredToken Then
                        ApplyTokenPlaceholder()
                    End If

                    ' Panels.
                    LoadPanelsList(db)
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to load Discord bot config:" & vbCrLf & ex.Message,
                    "Discord Bot", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End Try
        End Sub

        Private Sub LoadPanelsList(db As GsmDbContext)
            _panelsList.Items.Clear()
            Dim panels = db.DiscordPanels.OrderBy(Function(p) p.DisplayName).ToList()
            For Each p In panels
                Dim item As New ListViewItem(If(p.DisplayName, "(unnamed)"))
                item.SubItems.Add(GuildLabelFor(p.GuildId))
                item.SubItems.Add(ChannelLabelFor(p.GuildId, p.ChannelId))
                item.SubItems.Add(FormatScope(p.ScopeKind, p.ScopeTargetId))
                item.Tag = p.PanelId
                _panelsList.Items.Add(item)
            Next
            OnPanelsSelectionChanged(Me, EventArgs.Empty)
        End Sub

        Private Function GuildLabelFor(guildId As String) As String
            If String.IsNullOrEmpty(guildId) Then Return ""
            Try
                Dim plugin = ManagerProgram.Services.
                    GetService(Of DiscordBotPlugin)()
                If plugin Is Nothing Then Return guildId
                Dim guilds = plugin.GetGuildsAndChannels()
                Dim match = guilds.FirstOrDefault(Function(g) g.GuildId = guildId)
                If match IsNot Nothing Then Return match.Name
            Catch
            End Try
            Return guildId
        End Function

        Private Function ChannelLabelFor(guildId As String, channelId As String) As String
            If String.IsNullOrEmpty(channelId) Then Return ""
            Try
                Dim plugin = ManagerProgram.Services.
                    GetService(Of DiscordBotPlugin)()
                If plugin Is Nothing Then Return channelId
                Dim guilds = plugin.GetGuildsAndChannels()
                Dim guild = guilds.FirstOrDefault(Function(g) g.GuildId = guildId)
                If guild IsNot Nothing Then
                    Dim ch = guild.Channels.FirstOrDefault(Function(c) c.ChannelId = channelId)
                    If ch IsNot Nothing Then Return "#" & ch.Name
                End If
            Catch
            End Try
            Return channelId
        End Function

        Private Shared Function FormatScope(kind As String, targetId As String) As String
            Select Case (If(kind, "")).ToLowerInvariant()
                Case "allinstances", "" : Return "All instances"
                Case "game" : Return $"Game: {targetId}"
                Case "installation" : Return $"Installation: {targetId}"
                Case "instanceset" : Return $"Set: {targetId}"
                Case Else : Return $"{kind}: {targetId}"
            End Select
        End Function

        ' ============================================================
        '  Bot config — token textbox UX
        ' ============================================================

        Private Sub ApplyTokenPlaceholder()
            _tokenIsPlaceholder = True
            _tokenTextBox.UseSystemPasswordChar = False
            _tokenTextBox.ForeColor = SystemColors.GrayText
            _tokenTextBox.Text = TokenPlaceholder
            _showTokenButton.Enabled = False
        End Sub

        Private Sub ClearTokenPlaceholder()
            _tokenIsPlaceholder = False
            _tokenTextBox.ForeColor = SystemColors.WindowText
            _tokenTextBox.UseSystemPasswordChar = True
            _tokenTextBox.Text = ""
            _showTokenButton.Enabled = True
            _showTokenButton.Text = "Show"
        End Sub

        Private Sub OnTokenEnter(sender As Object, e As EventArgs)
            If _tokenIsPlaceholder Then ClearTokenPlaceholder()
        End Sub

        Private Sub OnTokenChanged(sender As Object, e As EventArgs)
            ' Once the user has typed a token, allow Show/Hide.
            ' Empty after typing is fine — we just won't change
            ' the stored token on save.
            If _tokenIsPlaceholder Then Return
            _showTokenButton.Enabled = (_tokenTextBox.Text.Length > 0)
        End Sub

        Private Sub OnToggleShowToken(sender As Object, e As EventArgs)
            If _tokenIsPlaceholder Then Return
            If _tokenTextBox.UseSystemPasswordChar Then
                _tokenTextBox.UseSystemPasswordChar = False
                _showTokenButton.Text = "Hide"
            Else
                _tokenTextBox.UseSystemPasswordChar = True
                _showTokenButton.Text = "Show"
            End If
        End Sub

        Private Function ResolveTokenForTest() As String
            ' Test should use whatever's in the box — if user typed
            ' something, that's the new token they want to verify.
            ' If the box is on placeholder (= use stored), decrypt
            ' the stored bytes.
            If Not _tokenIsPlaceholder AndAlso _tokenTextBox.Text.Length > 0 Then
                Return _tokenTextBox.Text
            End If

            If _hasStoredToken Then
                Try
                    Using scope = ManagerProgram.Services.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim cfg = db.DiscordBotConfigs.Find(DefaultConfigId)
                        If cfg IsNot Nothing Then
                            Return CredentialService.UnprotectString(cfg.EncryptedToken)
                        End If
                    End Using
                Catch
                End Try
            End If
            Return ""
        End Function

        ' ============================================================
        '  Bot config — buttons
        ' ============================================================

        Private Async Sub OnTestConnection(sender As Object, e As EventArgs)
            Dim token = ResolveTokenForTest()
            If String.IsNullOrWhiteSpace(token) Then
                _statusLabel.ForeColor = Color.DarkRed
                _statusLabel.Text = "Enter a token (or save one first) before testing."
                Return
            End If

            _testButton.Enabled = False
            _saveBotButton.Enabled = False
            _statusLabel.ForeColor = SystemColors.GrayText
            _statusLabel.Text = "Connecting to Discord…"

            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin Is Nothing Then
                _statusLabel.ForeColor = Color.DarkRed
                _statusLabel.Text = "Plugin not registered — restart the Manager."
                _testButton.Enabled = True
                _saveBotButton.Enabled = True
                Return
            End If

            Try
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(20))
                    Dim result = Await plugin.TestConnectionAsync(token, cts.Token)
                    If result.Success Then
                        _statusLabel.ForeColor = Color.DarkGreen
                        _statusLabel.Text = "✓ " & result.Message
                    Else
                        _statusLabel.ForeColor = Color.DarkRed
                        _statusLabel.Text = "✗ " & result.Message
                    End If
                End Using
            Catch ex As Exception
                _statusLabel.ForeColor = Color.DarkRed
                _statusLabel.Text = "Test failed: " & ex.Message
            Finally
                _testButton.Enabled = True
                _saveBotButton.Enabled = True
            End Try
        End Sub

        Private Async Sub OnSaveBotConfig(sender As Object, e As EventArgs)
            _saveBotButton.Enabled = False
            _testButton.Enabled = False
            Try
                Dim displayName = (_displayNameTextBox.Text & "").Trim()
                If String.IsNullOrEmpty(displayName) Then displayName = "PowerGSM Bot"

                Dim newTokenBytes As Byte() = Nothing
                Dim updateToken As Boolean = False
                If Not _tokenIsPlaceholder Then
                    Dim raw = _tokenTextBox.Text & ""
                    If raw.Length > 0 Then
                        Try
                            newTokenBytes = CredentialService.ProtectString(raw)
                        Catch ex As Exception
                            MessageBox.Show("Failed to encrypt token: " & ex.Message,
                                "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error)
                            Return
                        End Try
                        updateToken = True
                    End If
                    ' Empty textbox + not placeholder = user cleared
                    ' the token field after editing it. Keep the
                    ' stored token (don't blank it accidentally) —
                    ' clearing requires explicit action via a future
                    ' "Remove token" button if needed.
                End If

                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim now = DateTime.UtcNow
                    Dim cfg = db.DiscordBotConfigs.Find(DefaultConfigId)
                    If cfg Is Nothing Then
                        cfg = New DiscordBotConfigEntity With {
                            .ConfigId = DefaultConfigId,
                            .CreatedUtc = now
                        }
                        db.DiscordBotConfigs.Add(cfg)
                    End If
                    cfg.DisplayName = displayName
                    cfg.Enabled = _enabledCheckBox.Checked
                    If updateToken Then
                        cfg.EncryptedToken = newTokenBytes
                        _hasStoredToken = True
                    End If
                    cfg.UpdatedUtc = now
                    db.SaveChanges()
                End Using

                ' Restore the placeholder if the user just saved a
                ' new token — keeps the field secure visually.
                If updateToken Then ApplyTokenPlaceholder()

                _statusLabel.ForeColor = SystemColors.GrayText
                _statusLabel.Text = "Saved. Reconnecting bot…"

                ' Reload bot config (reconnects with new token /
                ' enable state). Best-effort — failures are
                ' surfaced via the status label.
                Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
                If plugin IsNot Nothing Then
                    Await plugin.ReloadConfigAsync()
                End If

                UpdateStatusLabel()
            Catch ex As Exception
                MessageBox.Show("Save failed: " & ex.Message,
                    "Discord Bot", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Finally
                _saveBotButton.Enabled = True
                _testButton.Enabled = True
            End Try
        End Sub

        Private Sub UpdateStatusLabel()
            Try
                Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
                If plugin Is Nothing Then
                    _statusLabel.ForeColor = SystemColors.GrayText
                    _statusLabel.Text = "Bot plugin is not registered."
                    Return
                End If

                ' Connected path — unchanged from the original
                ' Phase 5d-5 item 5 design: green check + uptime
                ' counter on the second line. ConnectedSinceUtc
                ' can briefly be Nothing in the race window
                ' between IsConnected flipping to True and
                ' _connectedSinceUtc being assigned; fall back to
                ' the bare "connected" line in that case so we
                ' don't print a misleading counter.
                If plugin.IsConnected Then
                    _statusLabel.ForeColor = Color.DarkGreen
                    Dim since = plugin.ConnectedSinceUtc
                    If since.HasValue Then
                        _statusLabel.Text =
                            "✓ Connected to Discord." & vbCrLf &
                            $"Connected for {FormatUptime(DateTime.UtcNow - since.Value)} (since {since.Value.ToLocalTime():HH:mm} local)."
                    Else
                        _statusLabel.Text = "✓ Connected to Discord."
                    End If
                    Return
                End If

                ' Token rejected — reconnect loop has exited.
                ' Only a token change (which restarts the loop
                ' via ReloadConfigAsync) can recover.
                If plugin.IsTokenRejected Then
                    _statusLabel.ForeColor = Color.DarkRed
                    _statusLabel.Text =
                        "✗ Discord rejected the configured token (401)." & vbCrLf &
                        "Enter a new token above and click Save Bot Config to retry."
                    Return
                End If

                ' Either rate-limited (waiting on Discord's
                ' Retry-After) or in a generic-failure backoff.
                ' Both surface a countdown to the next attempt;
                ' the leading icon and tone differ so the operator
                ' can tell at a glance whether they're rate-
                ' limited (transient, no action needed) vs. a
                ' generic failure they may want to investigate.
                Dim nextAttempt = plugin.NextConnectAttemptUtc
                If nextAttempt.HasValue Then
                    Dim remaining = nextAttempt.Value - DateTime.UtcNow
                    Dim secs = Math.Max(0, CInt(Math.Ceiling(remaining.TotalSeconds)))
                    If plugin.IsRateLimited Then
                        _statusLabel.ForeColor = Color.DarkOrange
                        _statusLabel.Text =
                            $"⏳ Rate-limited by Discord. Next attempt in {FormatCountdown(secs)}." & vbCrLf &
                            "Discord's Retry-After header drives this wait — no action needed; the bot will reconnect automatically."
                    Else
                        _statusLabel.ForeColor = SystemColors.GrayText
                        _statusLabel.Text =
                            $"Disconnected. Next attempt in {FormatCountdown(secs)}." & vbCrLf &
                            "Click Test Connection to retry now without waiting."
                    End If
                    Return
                End If

                ' No countdown scheduled — either the loop is
                ' actively in a connect attempt right now (brief
                ' window between attempts) or it hasn't started.
                _statusLabel.ForeColor = SystemColors.GrayText
                _statusLabel.Text = "Connecting to Discord…"
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Format a remaining-seconds countdown for the status
        ''' label. Sub-minute values render as "42s"; minute-plus
        ''' values render as "5m 03s" so the operator can see at
        ''' a glance whether they're staring at a long wait or a
        ''' short one. Caller passes a non-negative integer.
        ''' </summary>
        Private Shared Function FormatCountdown(secs As Integer) As String
            If secs < 60 Then Return $"{secs}s"
            Dim m = secs \ 60
            Dim s = secs Mod 60
            Return $"{m}m {s:D2}s"
        End Function

        ''' <summary>
        ''' Format an elapsed TimeSpan as a compact human string
        ''' for the uptime display. Granularity steps up with
        ''' magnitude: seconds while &lt; 1 minute, minutes &lt; 1
        ''' hour, then "Hh Mm" up through days. Below seconds
        ''' returns "just now" — the poll interval is 1s and a
        ''' "0s" reading would just be visual noise.
        ''' </summary>
        Private Shared Function FormatUptime(span As TimeSpan) As String
            If span.TotalSeconds < 1 Then Return "just now"
            If span.TotalMinutes < 1 Then Return $"{CInt(span.TotalSeconds)}s"
            If span.TotalHours < 1 Then Return $"{CInt(span.TotalMinutes)}m"
            If span.TotalDays < 1 Then Return $"{CInt(Math.Floor(span.TotalHours))}h {span.Minutes}m"
            Return $"{CInt(Math.Floor(span.TotalDays))}d {span.Hours}h"
        End Function

        ' ============================================================
        '  Role Mappings — modal launcher
        ' ============================================================

        Private Sub OnRoleMappings(sender As Object, e As EventArgs)
            ' DiscordRoleMappingsForm self-loads everything from
            ' the DB (and via DiscordBotPlugin.GetGuildsAndChannels /
            ' GetGuildRoles for live role enumeration), so we just
            ' open it. Each Add/Edit/Remove inside it auto-commits
            ' and triggers ReloadRoleMappingsAsync, so on close
            ' there's no pending state on this side to flush.
            Using dialog As New DiscordRoleMappingsForm()
                dialog.ShowDialog(Me)
            End Using
        End Sub

        ' ============================================================
        '  Panels — buttons
        ' ============================================================

        Private Sub OnPanelsSelectionChanged(sender As Object, e As EventArgs)
            Dim hasSelection = _panelsList.SelectedItems.Count > 0
            _editPanelButton.Enabled = hasSelection
            _removePanelButton.Enabled = hasSelection
        End Sub

        Private Sub OnAddPanel(sender As Object, e As EventArgs)
            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            Dim guilds As IReadOnlyList(Of GuildInfo) = New List(Of GuildInfo)
            If plugin IsNot Nothing Then guilds = plugin.GetGuildsAndChannels()

            Dim entity As New DiscordPanelEntity With {
                .PanelId = Guid.NewGuid().ToString("N"),
                .ScopeKind = "AllInstances",
                .RefreshIntervalSeconds = 60,
                .CreatedUtc = DateTime.UtcNow,
                .UpdatedUtc = DateTime.UtcNow
            }

            Using dialog As New DiscordPanelEditorForm(entity, isAdd:=True, guilds:=guilds)
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                Dim result = dialog.ResultPanel
                Try
                    Using scope = ManagerProgram.Services.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        db.DiscordPanels.Add(result)
                        db.SaveChanges()
                    End Using
                Catch ex As Exception
                    MessageBox.Show("Failed to save panel: " & ex.Message,
                        "Add panel", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End Try
                ReloadPanelsAndKickRefresh()
            End Using
        End Sub

        Private Sub OnEditPanel(sender As Object, e As EventArgs)
            If _panelsList.SelectedItems.Count = 0 Then Return
            Dim panelId = TryCast(_panelsList.SelectedItems(0).Tag, String)
            If String.IsNullOrEmpty(panelId) Then Return

            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            Dim guilds As IReadOnlyList(Of GuildInfo) = New List(Of GuildInfo)
            If plugin IsNot Nothing Then guilds = plugin.GetGuildsAndChannels()

            Dim entity As DiscordPanelEntity = Nothing
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    entity = db.DiscordPanels.Find(panelId)
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to load panel: " & ex.Message,
                    "Edit panel", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try
            If entity Is Nothing Then
                MessageBox.Show("Panel no longer exists.",
                    "Edit panel", MessageBoxButtons.OK, MessageBoxIcon.Information)
                ReloadPanelsAndKickRefresh()
                Return
            End If

            Using dialog As New DiscordPanelEditorForm(entity, isAdd:=False, guilds:=guilds)
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                Dim result = dialog.ResultPanel
                Try
                    Using scope = ManagerProgram.Services.CreateScope()
                        Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                        Dim row = db.DiscordPanels.Find(result.PanelId)
                        If row Is Nothing Then
                            ' Concurrent delete from another session;
                            ' re-insert with the user's edits.
                            db.DiscordPanels.Add(result)
                        Else
                            row.GuildId = result.GuildId
                            row.ChannelId = result.ChannelId
                            row.MessageId = result.MessageId
                            row.DisplayName = result.DisplayName
                            row.ScopeKind = result.ScopeKind
                            row.ScopeTargetId = result.ScopeTargetId
                            row.RefreshIntervalSeconds = result.RefreshIntervalSeconds
                            ' Phase 5d-5 item 3: layout + grouping.
                            ' Easy to forget when adding fields
                            ' to DiscordPanelEntity — the Edit path
                            ' field-copies onto a freshly-fetched
                            ' row instead of attaching the form's
                            ' entity directly, so any new column
                            ' has to be added here too or it
                            ' silently won't persist.
                            row.LayoutJson = result.LayoutJson
                            row.GroupingKind = result.GroupingKind
                            row.UpdatedUtc = DateTime.UtcNow
                        End If
                        db.SaveChanges()
                    End Using
                Catch ex As Exception
                    MessageBox.Show("Failed to save panel: " & ex.Message,
                        "Edit panel", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End Try
                ReloadPanelsAndKickRefresh()
            End Using
        End Sub

        Private Sub OnRemovePanel(sender As Object, e As EventArgs)
            If _panelsList.SelectedItems.Count = 0 Then Return
            Dim panelId = TryCast(_panelsList.SelectedItems(0).Tag, String)
            If String.IsNullOrEmpty(panelId) Then Return

            Dim confirm = MessageBox.Show(
                "Remove this panel? The Discord message it posted will not be deleted automatically — clean it up manually if needed.",
                "Remove panel", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If confirm <> DialogResult.Yes Then Return

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.DiscordPanels.Find(panelId)
                    If row IsNot Nothing Then
                        db.DiscordPanels.Remove(row)
                        db.SaveChanges()
                    End If
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to remove panel: " & ex.Message,
                    "Remove panel", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End Try
            ReloadPanelsAndKickRefresh()
        End Sub

        Private Sub ReloadPanelsAndKickRefresh()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    LoadPanelsList(db)
                End Using
            Catch ex As Exception
                MessageBox.Show("Failed to reload panel list: " & ex.Message,
                    "Discord Bot", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End Try

            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin IsNot Nothing Then plugin.RequestRefreshAllPanels()
        End Sub

    End Class

End Namespace
