Imports System
Imports System.ComponentModel
Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Windows.Forms

' ============================================================
'  MainSetupForm — WinForms GUI for the setup tool
'
'  Single-form, four-tab design built entirely in code (no .resx
'  designer file) to match the existing pattern of MainForm.vb in
'  GSM.Manager. Tabs:
'    1. General   — node identity, port, paths, limits
'    2. Auth Token — view, copy, regenerate
'    3. Security  — advanced limits, hidden behind a tab
'    4. Service   — install/uninstall/start/stop service
'
'  The form binds to a NodeSetupConfig instance loaded on construction
'  and re-saves on Save / Save & Exit. Cancel discards in-memory edits.
'  The instance is the single source of truth — controls read from it
'  on activation and write to it on save, no two-way binding.
' ============================================================

Namespace Windows

    Public Class MainSetupForm
        Inherits Form

        Private ReadOnly _configPath As String
        Private _config As NodeSetupConfig

        ' Status / path strip
        Private _pathLabel As Label
        Private _statusLabel As Label

        ' General tab
        Private _nodeIdBox As TextBox
        Private _listenPortBox As NumericUpDown
        Private _dataDirBox As TextBox
        Private _dataDirBrowse As Button
        Private _serversDirBox As TextBox
        Private _serversDirBrowse As Button
        Private _maxConcurrentBox As NumericUpDown
        Private _logRetentionBox As NumericUpDown
        Private _metricsIntervalBox As NumericUpDown

        ' Auth token tab
        Private _tokenBox As TextBox
        Private _showTokenCheck As CheckBox
        Private _copyTokenButton As Button
        Private _regenTokenButton As Button

        ' Security tab
        Private _maxFailedBox As NumericUpDown
        Private _failureWindowBox As NumericUpDown
        Private _lockoutBox As NumericUpDown
        Private _delayBox As NumericUpDown
        Private _rpmBox As NumericUpDown
        Private _maxBodyBox As NumericUpDown
        Private _maxConnBox As NumericUpDown
        Private _resetSecurityButton As Button

        ' Service tab
        Private _serviceStatusLabel As Label
        Private _serviceNameBox As TextBox
        Private _serviceDisplayBox As TextBox
        Private _serviceInstallButton As Button
        Private _serviceUninstallButton As Button
        Private _serviceStartButton As Button
        Private _serviceStopButton As Button
        Private _serviceRefreshButton As Button
        Private _serviceLog As TextBox

        ' Bottom action bar
        Private _generateTokenButton As Button
        Private _saveButton As Button
        Private _saveExitButton As Button
        Private _cancelButton As Button

        Public Sub New(configPath As String)
            _configPath = configPath
            _config = NodeSetupConfig.LoadOrCreate(configPath)
            BuildUi()
            LoadConfigIntoControls()
            UpdateStatusLabel()
            RefreshServiceStatus()
        End Sub

        ' --------------------------------------------------------
        ' UI construction
        ' --------------------------------------------------------

        Private Sub BuildUi()
            Me.Text = Program.ProductName
            ' Form sized to fit the General tab's seven content rows plus
            ' the help-text row — was 720x640 with six content rows; the
            ' addition of Servers directory bumps it by one ~32px row.
            Me.MinimumSize = New Size(680, 660)
            Me.Size = New Size(720, 680)
            Me.StartPosition = FormStartPosition.CenterScreen
            Me.Font = New Font("Segoe UI", 9.0F)

            ' Top panel: config path + status
            Dim topPanel As New TableLayoutPanel() With {
                .Dock = DockStyle.Top,
                .AutoSize = True,
                .ColumnCount = 2,
                .RowCount = 2,
                .Padding = New Padding(10, 8, 10, 4)
            }
            topPanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            topPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))

            topPanel.Controls.Add(New Label() With {
                .Text = "Config file:",
                .AutoSize = True,
                .Anchor = AnchorStyles.Left,
                .Margin = New Padding(0, 6, 6, 0)
            }, 0, 0)
            _pathLabel = New Label() With {
                .Text = _configPath,
                .AutoSize = True,
                .Anchor = AnchorStyles.Left,
                .Margin = New Padding(0, 6, 0, 0)
            }
            topPanel.Controls.Add(_pathLabel, 1, 0)

            topPanel.Controls.Add(New Label() With {
                .Text = "Status:",
                .AutoSize = True,
                .Anchor = AnchorStyles.Left,
                .Margin = New Padding(0, 6, 6, 0)
            }, 0, 1)
            _statusLabel = New Label() With {
                .Text = "",
                .AutoSize = True,
                .Anchor = AnchorStyles.Left,
                .Font = New Font(Me.Font, FontStyle.Bold),
                .Margin = New Padding(0, 6, 0, 0)
            }
            topPanel.Controls.Add(_statusLabel, 1, 1)
            Me.Controls.Add(topPanel)

            ' Bottom action bar (added before tabs so Dock=Fill on tabs
            ' uses the remaining space correctly).
            Dim bottomPanel = BuildBottomPanel()
            Me.Controls.Add(bottomPanel)

            ' Tabs
            Dim tabs As New TabControl() With {
                .Dock = DockStyle.Fill,
                .Padding = New Point(12, 4)
            }
            tabs.TabPages.Add(BuildGeneralTab())
            tabs.TabPages.Add(BuildAuthTokenTab())
            tabs.TabPages.Add(BuildSecurityTab())
            tabs.TabPages.Add(BuildServiceTab())
            Me.Controls.Add(tabs)

            ' Z-order: tabs go in last so they Fill above the bottom panel.
            tabs.BringToFront()
        End Sub

        Private Function BuildGeneralTab() As TabPage
            Dim tab As New TabPage("General")
            ' Eight rows: seven fields + one help-text row at the bottom.
            ' Was seven (six fields + help) before Servers directory.
            Dim grid = MakeFieldGrid(8)

            AddRow(grid, 0, "Node ID:", BuildNodeIdBox())
            AddRow(grid, 1, "Listen port:", BuildListenPortBox())
            AddRow(grid, 2, "Data directory:", BuildDataDirBox())
            AddRow(grid, 3, "Servers directory:", BuildServersDirBox())
            AddRow(grid, 4, "Max concurrent installs:", BuildMaxConcurrentBox())
            AddRow(grid, 5, "Log retention (days):", BuildLogRetentionBox())
            AddRow(grid, 6, "Metrics interval (seconds):", BuildMetricsIntervalBox())

            ' Helpful explanatory text in the last row, spanning both columns.
            Dim helpLabel As New Label() With {
                .Text = "These are the core operational settings for this node. " &
                        "Defaults are sensible — change only what your deployment needs.",
                .ForeColor = SystemColors.GrayText,
                .AutoSize = False,
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Padding = New Padding(0, 12, 0, 0)
            }
            grid.SetColumnSpan(helpLabel, 2)
            grid.Controls.Add(helpLabel, 0, 7)

            tab.Controls.Add(grid)
            Return tab
        End Function

        Private Function BuildNodeIdBox() As Control
            _nodeIdBox = New TextBox() With {
                .Anchor = AnchorStyles.Left Or AnchorStyles.Right,
                .Width = 320
            }
            Return _nodeIdBox
        End Function

        Private Function BuildListenPortBox() As Control
            _listenPortBox = New NumericUpDown() With {
                .Minimum = 1,
                .Maximum = 65535,
                .Width = 100,
                .Anchor = AnchorStyles.Left
            }
            Return _listenPortBox
        End Function

        Private Function BuildDataDirBox() As Control
            ' Composite: TextBox + Browse button.
            ' We use a 2-column TableLayoutPanel rather than a FlowLayoutPanel
            ' because we want the textbox to stretch with the column width
            ' (Anchor=Left|Right) while the button stays a fixed 80px on the
            ' right. A FlowLayoutPanel can't do that — it always sizes its
            ' children to their preferred size and lets them overflow the
            ' form, which is what was clipping the Browse button off-screen.
            _dataDirBox = New TextBox()
            _dataDirBrowse = New Button()
            Return BuildPathPickerRow(_dataDirBox, _dataDirBrowse, AddressOf OnDataDirBrowseClick)
        End Function

        ''' <summary>
        ''' Same TextBox + Browse layout as BuildDataDirBox. Factored
        ''' into BuildPathPickerRow so adding a third path field later
        ''' (or tweaking the layout) only needs one edit. Browse handler
        ''' is per-field so each can seed its FolderBrowserDialog with
        ''' its own current value.
        ''' </summary>
        Private Function BuildServersDirBox() As Control
            _serversDirBox = New TextBox()
            _serversDirBrowse = New Button()
            Return BuildPathPickerRow(_serversDirBox, _serversDirBrowse, AddressOf OnServersDirBrowseClick)
        End Function

        ''' <summary>
        ''' Build the TextBox + Browse-button composite used for any
        ''' path-pickable field on the General tab. Caller supplies the
        ''' control instances (so the form can hold field references)
        ''' and the browse-click handler.
        ''' </summary>
        Private Function BuildPathPickerRow(textBox As TextBox,
                                              browseButton As Button,
                                              browseHandler As EventHandler) As Control
            Dim layout As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1,
                .Margin = New Padding(0),
                .Padding = New Padding(0),
                .AutoSize = True,
                .AutoSizeMode = AutoSizeMode.GrowAndShrink
            }
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            layout.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))

            textBox.Anchor = AnchorStyles.Left Or AnchorStyles.Right
            textBox.Margin = New Padding(0, 0, 6, 0)

            browseButton.Text = "Browse..."
            browseButton.Width = 80
            browseButton.Anchor = AnchorStyles.Right
            browseButton.Margin = New Padding(0)
            AddHandler browseButton.Click, browseHandler

            layout.Controls.Add(textBox, 0, 0)
            layout.Controls.Add(browseButton, 1, 0)
            Return layout
        End Function

        Private Function BuildMaxConcurrentBox() As Control
            _maxConcurrentBox = New NumericUpDown() With {
                .Minimum = 1, .Maximum = 100, .Width = 80, .Anchor = AnchorStyles.Left
            }
            Return _maxConcurrentBox
        End Function

        Private Function BuildLogRetentionBox() As Control
            _logRetentionBox = New NumericUpDown() With {
                .Minimum = 1, .Maximum = 3650, .Width = 80, .Anchor = AnchorStyles.Left
            }
            Return _logRetentionBox
        End Function

        Private Function BuildMetricsIntervalBox() As Control
            _metricsIntervalBox = New NumericUpDown() With {
                .Minimum = 1, .Maximum = 3600, .Width = 80, .Anchor = AnchorStyles.Left
            }
            Return _metricsIntervalBox
        End Function

        Private Function BuildAuthTokenTab() As TabPage
            Dim tab As New TabPage("Auth Token")
            Dim grid As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 4,
                .Padding = New Padding(16, 16, 16, 16)
            }
            grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            ' Row sizing: label, textbox, buttons row, then a percent-fill
            ' row that holds the help text. Putting help in the fill row
            ' (rather than a separate spacer-then-help arrangement) lets
            ' it expand to use whatever vertical space the tab has, and
            ' AutoSize=True on the Label means it never clips its text
            ' even at the form's minimum height.
            grid.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            grid.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            grid.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            grid.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            grid.Controls.Add(New Label() With {
                .Text = "Authentication token",
                .Font = New Font(Me.Font, FontStyle.Bold),
                .AutoSize = True
            }, 0, 0)

            _tokenBox = New TextBox() With {
                .Anchor = AnchorStyles.Left Or AnchorStyles.Right,
                .Font = New Font("Consolas", 9.5F),
                .UseSystemPasswordChar = True,
                .ReadOnly = False,
                .Width = 600
            }
            grid.Controls.Add(_tokenBox, 0, 1)

            ' Buttons row. FlowDirection=RightToLeft so the buttons hug the
            ' right edge of the tab (and align with the right edge of the
            ' stretched textbox above them), matching the Windows convention
            ' for action buttons that operate on the control above. Adding
            ' first goes rightmost, so the visual order ends up:
            '   [Show token]            [Copy] [Generate new token]
            ' with Show token still on the left (added last, anchored away
            ' from the flow direction).
            Dim buttonsFlow As New FlowLayoutPanel() With {
                .FlowDirection = FlowDirection.RightToLeft,
                .Dock = DockStyle.Top,
                .AutoSize = True,
                .WrapContents = False,
                .Margin = New Padding(0, 6, 0, 0)
            }
            _showTokenCheck = New CheckBox() With {
                .Text = "Show token",
                .AutoSize = True,
                .Anchor = AnchorStyles.Left,
                .Margin = New Padding(0, 4, 16, 0)
            }
            AddHandler _showTokenCheck.CheckedChanged, AddressOf OnShowTokenChanged

            _copyTokenButton = New Button() With {.Text = "Copy", .Width = 90, .Margin = New Padding(0, 0, 8, 0)}
            AddHandler _copyTokenButton.Click, AddressOf OnCopyTokenClick

            _regenTokenButton = New Button() With {
                .Text = "Generate new token",
                .Width = 160,
                .Margin = New Padding(0)
            }
            AddHandler _regenTokenButton.Click, AddressOf OnRegenTokenClick

            ' RightToLeft flow: first added is rightmost.
            buttonsFlow.Controls.Add(_regenTokenButton)
            buttonsFlow.Controls.Add(_copyTokenButton)
            buttonsFlow.Controls.Add(_showTokenCheck)
            grid.Controls.Add(buttonsFlow, 0, 2)

            ' Help text. Dock=Top + AutoSize ensures the label grows to
            ' fit its wrapped text (MaximumSize caps width so it wraps
            ' rather than running off the right edge), so it can never
            ' be vertically clipped by a too-small form. The previous
            ' Dock=Fill version was being squeezed by the row layout
            ' when the form was at its minimum height.
            Dim helpText As New Label() With {
                .Text = "This token is required by the Manager to connect to this node. " &
                        "Keep it secret — anyone with this token can control the node. " &
                        "You can paste it into the Manager when adding this node, or copy it later from this screen.",
                .AutoSize = True,
                .MaximumSize = New Size(620, 0),
                .Dock = DockStyle.Top,
                .ForeColor = SystemColors.GrayText,
                .Padding = New Padding(0, 16, 0, 0)
            }
            grid.Controls.Add(helpText, 0, 3)

            tab.Controls.Add(grid)
            Return tab
        End Function

        Private Function BuildSecurityTab() As TabPage
            Dim tab As New TabPage("Security")
            Dim grid = MakeFieldGrid(9)

            ' Banner spanning both columns.
            Dim banner As New Label() With {
                .Text = "Advanced — defaults are good for most setups. Change with care.",
                .ForeColor = Color.DarkGoldenrod,
                .Font = New Font(Me.Font, FontStyle.Bold),
                .AutoSize = False,
                .Dock = DockStyle.Fill,
                .Padding = New Padding(0, 0, 0, 8)
            }
            grid.SetColumnSpan(banner, 2)
            grid.Controls.Add(banner, 0, 0)

            _maxFailedBox = SimpleNumericUpDown(1, 1000, 80)
            _failureWindowBox = SimpleNumericUpDown(1, 1440, 80)
            _lockoutBox = SimpleNumericUpDown(1, 1440, 80)
            _delayBox = SimpleNumericUpDown(0, 60000, 100)
            _rpmBox = SimpleNumericUpDown(0, 100000, 100)
            _maxBodyBox = SimpleNumericUpDown(1024, Integer.MaxValue, 140)
            _maxConnBox = SimpleNumericUpDown(1, 10000, 80)

            AddRow(grid, 1, "Max failed auth attempts:", _maxFailedBox)
            AddRow(grid, 2, "Failure window (minutes):", _failureWindowBox)
            AddRow(grid, 3, "Lockout duration (minutes):", _lockoutBox)
            AddRow(grid, 4, "Auth failure delay (ms):", _delayBox)
            AddRow(grid, 5, "Requests per minute per IP:", _rpmBox)
            AddRow(grid, 6, "Max request body (bytes):", _maxBodyBox)
            AddRow(grid, 7, "Max concurrent connections:", _maxConnBox)

            _resetSecurityButton = New Button() With {.Text = "Reset to defaults", .Width = 160}
            AddHandler _resetSecurityButton.Click, AddressOf OnResetSecurityClick
            grid.SetColumnSpan(_resetSecurityButton, 2)
            grid.Controls.Add(_resetSecurityButton, 0, 8)

            tab.Controls.Add(grid)
            Return tab
        End Function

        Private Function BuildServiceTab() As TabPage
            Dim tab As New TabPage("Service")

            Dim outer As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 4,
                .Padding = New Padding(16, 16, 16, 16)
            }
            outer.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            outer.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            outer.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            outer.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            outer.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            ' Status header
            Dim header As New TableLayoutPanel() With {
                .ColumnCount = 2, .RowCount = 1, .AutoSize = True, .Dock = DockStyle.Top
            }
            header.Controls.Add(New Label() With {
                .Text = "Status:", .AutoSize = True, .Anchor = AnchorStyles.Left,
                .Margin = New Padding(0, 6, 6, 0)
            }, 0, 0)
            _serviceStatusLabel = New Label() With {
                .Text = "(checking...)", .AutoSize = True, .Anchor = AnchorStyles.Left,
                .Font = New Font(Me.Font, FontStyle.Bold), .Margin = New Padding(0, 6, 0, 0)
            }
            header.Controls.Add(_serviceStatusLabel, 1, 0)
            outer.Controls.Add(header, 0, 0)

            ' Name fields. The label/value layout uses fixed column widths
            ' so it visually matches the buttons row below. Important: do
            ' NOT set AutoSizeMode=GrowAndShrink here — that collapses the
            ' Absolute=120 label column down to its content width, which
            ' then puts the textboxes at x=~100 and breaks alignment with
            ' the buttons row.
            Const labelColWidth As Integer = 120
            Dim nameGrid As New TableLayoutPanel() With {
                .Dock = DockStyle.Top,
                .ColumnCount = 2,
                .RowCount = 2,
                .AutoSize = True,
                .Margin = New Padding(0, 0, 0, 4)
            }
            nameGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, labelColWidth))
            nameGrid.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            nameGrid.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            nameGrid.RowStyles.Add(New RowStyle(SizeType.AutoSize))

            _serviceNameBox = New TextBox() With {.Width = 240, .Anchor = AnchorStyles.Left}
            _serviceDisplayBox = New TextBox() With {.Width = 320, .Anchor = AnchorStyles.Left}
            _serviceNameBox.Text = ServiceManager.DefaultServiceName
            _serviceDisplayBox.Text = ServiceManager.DefaultDisplayName
            AddRow(nameGrid, 0, "Service name:", _serviceNameBox)
            AddRow(nameGrid, 1, "Display name:", _serviceDisplayBox)
            outer.Controls.Add(nameGrid, 0, 1)

            ' Buttons row. Margin-left equals the label-column width so the
            ' first button (Install) lines up with the left edge of the
            ' Service name / Display name textboxes above. Margin-right
            ' of 16 keeps the rightmost button (Refresh) clear of the form
            ' edge even when the form is at its minimum width.
            Dim buttons As New FlowLayoutPanel() With {
                .FlowDirection = FlowDirection.LeftToRight,
                .AutoSize = True,
                .WrapContents = False,
                .Margin = New Padding(labelColWidth, 8, 16, 8)
            }
            ' Tighter, consistent 6px gaps between buttons (was a mix of
            ' 8 and 16 — the 16s created visual "groups" that didn't match
            ' any logical grouping; all five buttons are peer actions).
            _serviceInstallButton = New Button() With {.Text = "Install", .Width = 90, .Margin = New Padding(0, 0, 0, 0)}
            _serviceUninstallButton = New Button() With {.Text = "Uninstall", .Width = 90, .Margin = New Padding(6, 0, 0, 0)}
            _serviceStartButton = New Button() With {.Text = "Start", .Width = 70, .Margin = New Padding(6, 0, 0, 0)}
            _serviceStopButton = New Button() With {.Text = "Stop", .Width = 70, .Margin = New Padding(6, 0, 0, 0)}
            _serviceRefreshButton = New Button() With {.Text = "Refresh", .Width = 90, .Margin = New Padding(6, 0, 0, 0)}
            AddHandler _serviceInstallButton.Click, AddressOf OnInstallServiceClick
            AddHandler _serviceUninstallButton.Click, AddressOf OnUninstallServiceClick
            AddHandler _serviceStartButton.Click, AddressOf OnStartServiceClick
            AddHandler _serviceStopButton.Click, AddressOf OnStopServiceClick
            AddHandler _serviceRefreshButton.Click, AddressOf OnRefreshServiceClick
            buttons.Controls.Add(_serviceInstallButton)
            buttons.Controls.Add(_serviceUninstallButton)
            buttons.Controls.Add(_serviceStartButton)
            buttons.Controls.Add(_serviceStopButton)
            buttons.Controls.Add(_serviceRefreshButton)
            outer.Controls.Add(buttons, 0, 2)

            ' Output log
            _serviceLog = New TextBox() With {
                .Multiline = True,
                .ScrollBars = ScrollBars.Vertical,
                .ReadOnly = True,
                .Dock = DockStyle.Fill,
                .Font = New Font("Consolas", 9.0F),
                .BackColor = Color.WhiteSmoke
            }
            outer.Controls.Add(_serviceLog, 0, 3)

            tab.Controls.Add(outer)
            Return tab
        End Function

        Private Function BuildBottomPanel() As Panel
            Dim panel As New Panel() With {
                .Dock = DockStyle.Bottom,
                .Height = 50,
                .Padding = New Padding(10, 8, 10, 8)
            }

            ' RightToLeft FlowLayoutPanel: first child added sits at the
            ' right. The panel's Padding(10,...) provides the gap between
            ' the rightmost button and the form's right edge — do NOT
            ' add an extra right margin to Cancel (that creates an
            ' inconsistent visual gap between Cancel and Save and Exit
            ' compared to the gaps between the other buttons).
            Dim flow As New FlowLayoutPanel() With {
                .FlowDirection = FlowDirection.RightToLeft,
                .Dock = DockStyle.Fill,
                .WrapContents = False,
                .Padding = New Padding(0)
            }

            _cancelButton = New Button() With {.Text = "Cancel", .Width = 90, .Margin = New Padding(8, 0, 0, 0)}
            _saveExitButton = New Button() With {.Text = "Save and Exit", .Width = 130, .Margin = New Padding(8, 0, 0, 0)}
            _saveButton = New Button() With {.Text = "Save", .Width = 90, .Margin = New Padding(8, 0, 0, 0)}
            _generateTokenButton = New Button() With {.Text = "Generate Token", .Width = 130, .Margin = New Padding(8, 0, 0, 0)}

            AddHandler _cancelButton.Click, AddressOf OnCancelClick
            AddHandler _saveExitButton.Click, AddressOf OnSaveExitClick
            AddHandler _saveButton.Click, AddressOf OnSaveClick
            AddHandler _generateTokenButton.Click, AddressOf OnRegenTokenClick

            ' Order matters with RightToLeft flow: first added sits at far right.
            flow.Controls.Add(_cancelButton)
            flow.Controls.Add(_saveExitButton)
            flow.Controls.Add(_saveButton)
            flow.Controls.Add(_generateTokenButton)
            panel.Controls.Add(flow)

            Me.AcceptButton = _saveButton
            Me.CancelButton = _cancelButton
            Return panel
        End Function

        ' --------------------------------------------------------
        ' Layout helpers
        ' --------------------------------------------------------

        Private Function MakeFieldGrid(rows As Integer) As TableLayoutPanel
            Dim grid As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = rows,
                .Padding = New Padding(16, 16, 16, 16)
            }
            grid.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 220))
            grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            For i = 0 To rows - 1
                grid.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            Next
            Return grid
        End Function

        Private Sub AddRow(grid As TableLayoutPanel, row As Integer, labelText As String, control As Control)
            Dim lbl As New Label() With {
                .Text = labelText,
                .AutoSize = True,
                .Anchor = AnchorStyles.Left,
                .Margin = New Padding(0, 6, 6, 6)
            }
            grid.Controls.Add(lbl, 0, row)
            control.Margin = New Padding(0, 4, 0, 4)
            grid.Controls.Add(control, 1, row)
        End Sub

        Private Function SimpleNumericUpDown(min As Decimal, max As Decimal, width As Integer) As NumericUpDown
            Return New NumericUpDown() With {
                .Minimum = min,
                .Maximum = max,
                .Width = width,
                .Anchor = AnchorStyles.Left
            }
        End Function

        ' --------------------------------------------------------
        ' Config <-> controls
        ' --------------------------------------------------------

        Private Sub LoadConfigIntoControls()
            _nodeIdBox.Text = If(String.IsNullOrEmpty(_config.Node.NodeId), Environment.MachineName, _config.Node.NodeId)
            _listenPortBox.Value = ClampDecimal(_config.Node.ListenPort, _listenPortBox.Minimum, _listenPortBox.Maximum)
            _dataDirBox.Text = _config.Node.DataDirectory
            _serversDirBox.Text = _config.Node.ServersDirectory
            _maxConcurrentBox.Value = ClampDecimal(_config.Node.MaxConcurrentInstalls, _maxConcurrentBox.Minimum, _maxConcurrentBox.Maximum)
            _logRetentionBox.Value = ClampDecimal(_config.Node.LogRetentionDays, _logRetentionBox.Minimum, _logRetentionBox.Maximum)
            _metricsIntervalBox.Value = ClampDecimal(_config.Node.MetricsIntervalSeconds, _metricsIntervalBox.Minimum, _metricsIntervalBox.Maximum)

            _tokenBox.Text = If(_config.NeedsAuthTokenSetup, "", _config.Node.AuthToken)

            _maxFailedBox.Value = ClampDecimal(_config.Security.MaxFailedAttempts, _maxFailedBox.Minimum, _maxFailedBox.Maximum)
            _failureWindowBox.Value = ClampDecimal(_config.Security.FailureWindowMinutes, _failureWindowBox.Minimum, _failureWindowBox.Maximum)
            _lockoutBox.Value = ClampDecimal(_config.Security.LockoutMinutes, _lockoutBox.Minimum, _lockoutBox.Maximum)
            _delayBox.Value = ClampDecimal(_config.Security.AuthFailureDelayMs, _delayBox.Minimum, _delayBox.Maximum)
            _rpmBox.Value = ClampDecimal(_config.Security.RequestsPerMinutePerIp, _rpmBox.Minimum, _rpmBox.Maximum)
            _maxBodyBox.Value = ClampDecimal(_config.Security.MaxRequestBodyBytes, _maxBodyBox.Minimum, _maxBodyBox.Maximum)
            _maxConnBox.Value = ClampDecimal(_config.Security.MaxConcurrentConnections, _maxConnBox.Minimum, _maxConnBox.Maximum)
        End Sub

        Private Function ClampDecimal(value As Long, minVal As Decimal, maxVal As Decimal) As Decimal
            Dim d As Decimal = CDec(value)
            If d < minVal Then Return minVal
            If d > maxVal Then Return maxVal
            Return d
        End Function

        ''' <summary>
        ''' Pull every value from controls back into the config object.
        ''' Returns Nothing on success or a validation error message.
        ''' </summary>
        Private Function CommitControlsToConfig() As String
            Dim nodeIdErr = ConfigHelpers.ValidateNodeId(_nodeIdBox.Text)
            If nodeIdErr IsNot Nothing AndAlso Not nodeIdErr.StartsWith("Warning") AndAlso Not nodeIdErr.StartsWith("Note") Then
                Return nodeIdErr
            End If
            Dim dataDirErr = ConfigHelpers.ValidateDataDirectory(_dataDirBox.Text)
            If dataDirErr IsNot Nothing AndAlso Not dataDirErr.StartsWith("Warning") AndAlso Not dataDirErr.StartsWith("Note") Then
                Return dataDirErr
            End If
            Dim serversDirErr = ConfigHelpers.ValidateServersDirectory(_serversDirBox.Text)
            If serversDirErr IsNot Nothing AndAlso Not serversDirErr.StartsWith("Warning") AndAlso Not serversDirErr.StartsWith("Note") Then
                Return serversDirErr
            End If

            _config.Node.NodeId = _nodeIdBox.Text.Trim()
            _config.Node.ListenPort = CInt(_listenPortBox.Value)
            _config.Node.DataDirectory = _dataDirBox.Text.Trim()
            _config.Node.ServersDirectory = _serversDirBox.Text.Trim()
            _config.Node.MaxConcurrentInstalls = CInt(_maxConcurrentBox.Value)
            _config.Node.LogRetentionDays = CInt(_logRetentionBox.Value)
            _config.Node.MetricsIntervalSeconds = CInt(_metricsIntervalBox.Value)

            ' AuthToken: if the field is empty, generate one. If the user
            ' edited it manually, accept the value as-is (advanced workflow).
            If String.IsNullOrWhiteSpace(_tokenBox.Text) Then
                _config.Node.AuthToken = ConfigHelpers.GenerateAuthToken()
                _tokenBox.Text = _config.Node.AuthToken
            Else
                _config.Node.AuthToken = _tokenBox.Text.Trim()
            End If

            _config.Security.MaxFailedAttempts = CInt(_maxFailedBox.Value)
            _config.Security.FailureWindowMinutes = CInt(_failureWindowBox.Value)
            _config.Security.LockoutMinutes = CInt(_lockoutBox.Value)
            _config.Security.AuthFailureDelayMs = CInt(_delayBox.Value)
            _config.Security.RequestsPerMinutePerIp = CInt(_rpmBox.Value)
            _config.Security.MaxRequestBodyBytes = CLng(_maxBodyBox.Value)
            _config.Security.MaxConcurrentConnections = CInt(_maxConnBox.Value)
            Return Nothing
        End Function

        Private Sub UpdateStatusLabel()
            If _config.NeedsAuthTokenSetup Then
                _statusLabel.Text = "NOT CONFIGURED  (auth token is the default)"
                _statusLabel.ForeColor = Color.DarkGoldenrod
            Else
                _statusLabel.Text = "Configured"
                _statusLabel.ForeColor = Color.ForestGreen
            End If
        End Sub

        ' --------------------------------------------------------
        ' Event handlers — General tab
        ' --------------------------------------------------------

        Private Sub OnDataDirBrowseClick(sender As Object, e As EventArgs)
            BrowseForDirectory(_dataDirBox, "Select a directory for node data")
        End Sub

        Private Sub OnServersDirBrowseClick(sender As Object, e As EventArgs)
            BrowseForDirectory(_serversDirBox,
                "Select the parent directory for game-server installations")
        End Sub

        ''' <summary>
        ''' Open a folder picker for the given textbox. Seeds the
        ''' dialog with the textbox's current value when it's a real
        ''' rooted path that exists on disk — relative paths and
        ''' missing directories let the dialog fall back to its
        ''' default location rather than throwing.
        ''' </summary>
        Private Sub BrowseForDirectory(target As TextBox, description As String)
            Using dlg As New FolderBrowserDialog()
                dlg.Description = description
                dlg.ShowNewFolderButton = True

                Try
                    Dim current = target.Text
                    If Not String.IsNullOrWhiteSpace(current) AndAlso Path.IsPathRooted(current) AndAlso Directory.Exists(current) Then
                        dlg.SelectedPath = current
                    End If
                Catch
                    ' Ignore — let the dialog open in its default location.
                End Try

                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    target.Text = dlg.SelectedPath
                End If
            End Using
        End Sub

        ' --------------------------------------------------------
        ' Event handlers — Auth Token tab
        ' --------------------------------------------------------

        Private Sub OnShowTokenChanged(sender As Object, e As EventArgs)
            _tokenBox.UseSystemPasswordChar = Not _showTokenCheck.Checked
        End Sub

        Private Sub OnCopyTokenClick(sender As Object, e As EventArgs)
            If String.IsNullOrEmpty(_tokenBox.Text) Then
                MessageBox.Show(Me, "There is no token to copy yet. Click 'Generate new token' first.",
                                "No token", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            Try
                Clipboard.SetText(_tokenBox.Text)
                ' Brief visual feedback by changing the button text temporarily.
                _copyTokenButton.Text = "Copied!"
                Dim t As New Timer() With {.Interval = 1200}
                AddHandler t.Tick, Sub(s2, e2)
                                       _copyTokenButton.Text = "Copy"
                                       t.Stop()
                                       t.Dispose()
                                   End Sub
                t.Start()
            Catch ex As Exception
                MessageBox.Show(Me, "Failed to copy to clipboard: " & ex.Message,
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Sub

        Private Sub OnRegenTokenClick(sender As Object, e As EventArgs)
            If Not String.IsNullOrWhiteSpace(_tokenBox.Text) Then
                Dim r = MessageBox.Show(Me,
                    "Generating a new token will disconnect the Manager until the new value is entered there." & vbCrLf & vbCrLf &
                    "Continue?",
                    "Confirm new token", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                If r <> DialogResult.Yes Then Return
            End If
            _tokenBox.Text = ConfigHelpers.GenerateAuthToken()
            ' Auto-show the freshly-generated token so the user can copy it.
            _showTokenCheck.Checked = True
        End Sub

        ' --------------------------------------------------------
        ' Event handlers — Security tab
        ' --------------------------------------------------------

        Private Sub OnResetSecurityClick(sender As Object, e As EventArgs)
            Dim r = MessageBox.Show(Me,
                "Reset all security settings to their defaults?",
                "Confirm reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If r <> DialogResult.Yes Then Return

            Dim defaults As New SecuritySection()
            _maxFailedBox.Value = defaults.MaxFailedAttempts
            _failureWindowBox.Value = defaults.FailureWindowMinutes
            _lockoutBox.Value = defaults.LockoutMinutes
            _delayBox.Value = defaults.AuthFailureDelayMs
            _rpmBox.Value = defaults.RequestsPerMinutePerIp
            _maxBodyBox.Value = defaults.MaxRequestBodyBytes
            _maxConnBox.Value = defaults.MaxConcurrentConnections
        End Sub

        ' --------------------------------------------------------
        ' Event handlers — Service tab
        ' --------------------------------------------------------

        Private Sub AppendServiceLog(text As String)
            If text Is Nothing Then Return
            If _serviceLog.TextLength > 0 Then
                _serviceLog.AppendText(Environment.NewLine)
            End If
            _serviceLog.AppendText(text)
        End Sub

        Private Sub OnInstallServiceClick(sender As Object, e As EventArgs)
            If Not ServiceManager.NodeExecutableExists() Then
                MessageBox.Show(Me,
                    "GSM.Node.exe was not found next to the setup tool." & vbCrLf &
                    "Expected at: " & ServiceManager.GetNodeExecutablePath() & vbCrLf & vbCrLf &
                    "The setup tool must be deployed alongside GSM.Node.",
                    "Cannot install", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            If Not ConfigHelpers.RunningElevated() Then
                MessageBox.Show(Me,
                    "Administrator rights are required to install a Windows service." & vbCrLf & vbCrLf &
                    "Close this tool and re-run it as Administrator (right-click -> Run as administrator).",
                    "Elevation required", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim r = MessageBox.Show(Me,
                "Install service '" & _serviceNameBox.Text & "' and start it?",
                "Confirm install", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If r <> DialogResult.Yes Then Return

            AppendServiceLog("Installing service...")
            Dim result = ServiceManager.InstallWindowsService(
                _serviceNameBox.Text, _serviceDisplayBox.Text, ServiceManager.DefaultDescription)
            AppendServiceLog(result.Message)
            If Not String.IsNullOrEmpty(result.Output) Then AppendServiceLog(result.Output)
            RefreshServiceStatus()
        End Sub

        Private Sub OnUninstallServiceClick(sender As Object, e As EventArgs)
            If Not ConfigHelpers.RunningElevated() Then
                MessageBox.Show(Me,
                    "Administrator rights are required to remove a Windows service.",
                    "Elevation required", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim r = MessageBox.Show(Me,
                "Stop and remove service '" & _serviceNameBox.Text & "'?",
                "Confirm uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If r <> DialogResult.Yes Then Return

            AppendServiceLog("Uninstalling service...")
            Dim result = ServiceManager.UninstallWindowsService(_serviceNameBox.Text)
            AppendServiceLog(result.Message)
            If Not String.IsNullOrEmpty(result.Output) Then AppendServiceLog(result.Output)
            RefreshServiceStatus()
        End Sub

        Private Sub OnStartServiceClick(sender As Object, e As EventArgs)
            RunScCommand("start", _serviceNameBox.Text)
        End Sub

        Private Sub OnStopServiceClick(sender As Object, e As EventArgs)
            RunScCommand("stop", _serviceNameBox.Text)
        End Sub

        Private Sub OnRefreshServiceClick(sender As Object, e As EventArgs)
            RefreshServiceStatus()
        End Sub

        Private Sub RunScCommand(verb As String, serviceName As String)
            Try
                Dim psi As New Diagnostics.ProcessStartInfo("sc.exe") With {
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
                psi.ArgumentList.Add(verb)
                psi.ArgumentList.Add(serviceName)
                Using proc = Diagnostics.Process.Start(psi)
                    Dim output = proc.StandardOutput.ReadToEnd() & proc.StandardError.ReadToEnd()
                    proc.WaitForExit()
                    AppendServiceLog($"sc {verb} {serviceName}:")
                    AppendServiceLog(output.Trim())
                End Using
            Catch ex As Exception
                AppendServiceLog("Error: " & ex.Message)
            End Try
            RefreshServiceStatus()
        End Sub

        Private Sub RefreshServiceStatus()
            Dim status As String
            If ConfigHelpers.RunningOnWindows() Then
                status = ServiceManager.GetWindowsServiceStatus(
                    If(_serviceNameBox IsNot Nothing AndAlso Not String.IsNullOrEmpty(_serviceNameBox.Text),
                       _serviceNameBox.Text, ServiceManager.DefaultServiceName))
            Else
                status = ServiceManager.GetSystemdStatus("gsmnode")
            End If

            If _serviceStatusLabel IsNot Nothing Then
                _serviceStatusLabel.Text = status
                Select Case status
                    Case "Running" : _serviceStatusLabel.ForeColor = Color.ForestGreen
                    Case "Stopped" : _serviceStatusLabel.ForeColor = Color.DarkGoldenrod
                    Case "NotInstalled" : _serviceStatusLabel.ForeColor = Color.Gray
                    Case "Failed" : _serviceStatusLabel.ForeColor = Color.Firebrick
                    Case Else : _serviceStatusLabel.ForeColor = SystemColors.ControlText
                End Select
            End If
        End Sub

        ' --------------------------------------------------------
        ' Bottom action bar
        ' --------------------------------------------------------

        Private Sub OnSaveClick(sender As Object, e As EventArgs)
            DoSave(closeAfter:=False)
        End Sub

        Private Sub OnSaveExitClick(sender As Object, e As EventArgs)
            DoSave(closeAfter:=True)
        End Sub

        Private Sub OnCancelClick(sender As Object, e As EventArgs)
            Me.Close()
        End Sub

        Private Sub DoSave(closeAfter As Boolean)
            Dim err = CommitControlsToConfig()
            If err IsNot Nothing Then
                MessageBox.Show(Me, err, "Validation error", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Try
                _config.Save(_configPath, backupExisting:=True)
            Catch ex As Exception
                MessageBox.Show(Me, "Failed to save: " & ex.Message,
                                "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error)
                Return
            End Try

            UpdateStatusLabel()

            If closeAfter Then
                Me.Close()
            Else
                MessageBox.Show(Me, "Configuration saved.", "Saved",
                                MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        End Sub

    End Class

End Namespace
