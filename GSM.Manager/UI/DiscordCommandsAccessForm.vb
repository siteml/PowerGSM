Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Notification

' ============================================================
'  DiscordCommandsAccessForm (Phase 5d-7c)
'
'  Read-only diagnostic surface for the Discord slash-command
'  model. Closes the gap 5d-7 targets: the command surface is
'  an emergent product of panel scopes (visibility) and role
'  mappings (permission), and until now nothing in the Manager
'  named it.
'
'  Two views:
'    1. Commands — rendered straight from SlashCommandCatalog
'       (the 5d-7a single source). Static per build: every
'       command, the permission tier it needs, and a one-line
'       "what it sees" note. Answers "what can the bot even do."
'    2. Per-server effective access — pick a server and see,
'       side by side, the two independent axes:
'         - Visible instances = GetInstancesVisibleInGuild (the
'           union of that server's panel scopes).
'         - Who can run them = the guild-DEFAULT role map
'           (DiscordRoleMappings rows with PanelId = "").
'       Per-panel overrides are deliberately NOT consulted here:
'       slash commands resolve against the guild-default map
'       only (verified — see Phase 5d-7 Decision #3), so showing
'       overrides would misrepresent what governs commands.
'
'  Everything is read-only; editing lives in the panel editor
'  (visibility) and the Role Mappings dialog (permission). This
'  window only reflects.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DiscordCommandsAccessForm
        Inherits Form

        Private _commandsList As ListView
        Private _guildCombo As ComboBox
        Private _visibleInstancesList As ListView
        Private _roleMapList As ListView
        Private _closeButton As Button

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            PopulateCommandsList()
            LoadGuilds()
        End Sub

        ' ============================================================
        '  Layout
        ' ============================================================

        Private Sub InitializeControls()
            Me.Text = "Discord Commands & Access"
            Me.Size = New Size(780, 640)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.MinimumSize = New Size(680, 560)

            Dim root As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 4,
                .Padding = New Padding(10)
            }
            root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            ' Row 0: intro (fixed). Row 1: commands list (fixed).
            ' Row 2: per-server preview (absorbs remaining space).
            ' Row 3: close button (fixed).
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 80))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 168))
            root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))

            ' Intro — names the two independent axes up front so the
            ' rest of the window reads as "these are different
            ' things", which is the whole point of the surface.
            Dim intro As New Label With {
                .Dock = DockStyle.Fill,
                .AutoSize = False,
                .TextAlign = ContentAlignment.TopLeft,
                .Text =
                    "Two independent things decide what the bot's slash commands do in a server:" & vbCrLf &
                    "   • Visibility — which instances a command can act on, from that server's panel scopes." & vbCrLf &
                    "   • Permission — who may run a command, from that server's role mappings." & vbCrLf &
                    "Pick a server below to see both, side by side, for that server."
            }
            root.Controls.Add(intro, 0, 0)

            root.Controls.Add(BuildCommandsGroup(), 0, 1)
            root.Controls.Add(BuildPerGuildGroup(), 0, 2)

            ' Close button row.
            Dim closeRow As New FlowLayoutPanel With {
                .Dock = DockStyle.Fill,
                .FlowDirection = FlowDirection.RightToLeft,
                .Padding = New Padding(0, 8, 0, 0)
            }
            _closeButton = New Button With {.Text = "Close", .Width = 100}
            AddHandler _closeButton.Click, Sub(s, e) Me.Close()
            closeRow.Controls.Add(_closeButton)
            root.Controls.Add(closeRow, 0, 3)

            Me.Controls.Add(root)
            Me.AcceptButton = _closeButton
            Me.CancelButton = _closeButton
        End Sub

        Private Function BuildCommandsGroup() As GroupBox
            Dim grp As New GroupBox With {
                .Text = "Commands (all servers)",
                .Dock = DockStyle.Fill,
                .Padding = New Padding(8)
            }
            _commandsList = New ListView With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .HideSelection = False,
                .MultiSelect = False,
                .HeaderStyle = ColumnHeaderStyle.Nonclickable
            }
            _commandsList.Columns.Add("Command", 130)
            _commandsList.Columns.Add("Requires", 150)
            _commandsList.Columns.Add("What it sees", 440)
            grp.Controls.Add(_commandsList)
            Return grp
        End Function

        Private Function BuildPerGuildGroup() As GroupBox
            Dim grp As New GroupBox With {
                .Text = "Per-server effective access",
                .Dock = DockStyle.Fill,
                .Padding = New Padding(8)
            }

            Dim inner As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 1,
                .RowCount = 2
            }
            inner.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            inner.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            inner.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            ' Server picker.
            Dim guildRow As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1
            }
            guildRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 56))
            guildRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            guildRow.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            guildRow.Controls.Add(New Label With {
                .Text = "Server:",
                .Dock = DockStyle.Fill,
                .TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0)
            _guildCombo = New ComboBox With {
                .Dock = DockStyle.Fill,
                .DropDownStyle = ComboBoxStyle.DropDownList
            }
            AddHandler _guildCombo.SelectedIndexChanged, AddressOf OnGuildChanged
            guildRow.Controls.Add(_guildCombo, 1, 0)
            inner.Controls.Add(guildRow, 0, 0)

            ' Two-column split: visible instances | role map. Shown
            ' side by side on purpose — the operator sees the two
            ' axes are distinct (one is "which instances", the other
            ' is "which people") rather than one combined notion.
            Dim split As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1
            }
            split.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
            split.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
            split.RowStyles.Add(New RowStyle(SizeType.Percent, 100))

            Dim visGroup As New GroupBox With {
                .Text = "Visible instances (from panel scopes)",
                .Dock = DockStyle.Fill,
                .Padding = New Padding(6)
            }
            _visibleInstancesList = New ListView With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .HideSelection = False,
                .MultiSelect = False,
                .HeaderStyle = ColumnHeaderStyle.Nonclickable
            }
            _visibleInstancesList.Columns.Add("Instance", 320)
            visGroup.Controls.Add(_visibleInstancesList)
            split.Controls.Add(visGroup, 0, 0)

            Dim permGroup As New GroupBox With {
                .Text = "Who can run commands (guild-default roles)",
                .Dock = DockStyle.Fill,
                .Padding = New Padding(6)
            }
            _roleMapList = New ListView With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .HideSelection = False,
                .MultiSelect = False,
                .HeaderStyle = ColumnHeaderStyle.Nonclickable
            }
            _roleMapList.Columns.Add("Role", 180)
            _roleMapList.Columns.Add("Permission", 150)
            permGroup.Controls.Add(_roleMapList)
            split.Controls.Add(permGroup, 1, 0)

            inner.Controls.Add(split, 0, 1)
            grp.Controls.Add(inner)
            Return grp
        End Function

        ' ============================================================
        '  Commands list — straight from the 5d-7a catalogue
        ' ============================================================

        Private Sub PopulateCommandsList()
            _commandsList.Items.Clear()
            For Each cmd In SlashCommandCatalog.All
                Dim item As New ListViewItem("/" & cmd.Name)
                item.SubItems.Add(PermissionLabel(cmd.MinimumPermission))
                item.SubItems.Add(cmd.VisibilityNote)
                _commandsList.Items.Add(item)
            Next
        End Sub

        ' ============================================================
        '  Server dropdown + per-server preview
        ' ============================================================

        Private Sub LoadGuilds()
            _guildCombo.Items.Clear()
            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin IsNot Nothing Then
                Try
                    For Each g In plugin.GetGuildsAndChannels()
                        _guildCombo.Items.Add(New GuildItem(g.GuildId, g.Name))
                    Next
                Catch
                    ' Best-effort — an empty dropdown falls through
                    ' to the "(not connected)" hint below.
                End Try
            End If

            If _guildCombo.Items.Count > 0 Then
                ' Assigning SelectedIndex fires OnGuildChanged, which
                ' populates both preview lists for the first server.
                _guildCombo.SelectedIndex = 0
            Else
                ShowNoGuilds()
            End If
        End Sub

        Private Sub ShowNoGuilds()
            _visibleInstancesList.Items.Clear()
            _roleMapList.Items.Clear()
            Dim row As New ListViewItem("(bot not connected to any server)")
            row.ForeColor = SystemColors.GrayText
            _visibleInstancesList.Items.Add(row)
        End Sub

        Private Sub OnGuildChanged(sender As Object, e As EventArgs)
            Dim guildItem = TryCast(_guildCombo.SelectedItem, GuildItem)
            If guildItem Is Nothing Then Return
            RefreshVisibleInstances(guildItem.GuildId)
            RefreshRoleMap(guildItem.GuildId)
        End Sub

        Private Sub RefreshVisibleInstances(guildId As String)
            _visibleInstancesList.Items.Clear()
            Dim plugin = ManagerProgram.Services.GetService(Of DiscordBotPlugin)()
            If plugin Is Nothing Then Return

            Dim entries = plugin.GetInstancesVisibleInGuild(guildId)
            If entries Is Nothing OrElse entries.Count = 0 Then
                Dim row As New ListViewItem("(none — this server has no panels, so commands return nothing)")
                row.ForeColor = SystemColors.GrayText
                _visibleInstancesList.Items.Add(row)
                Return
            End If

            For Each inst In entries.OrderBy(Function(x) x.DisplayName)
                Dim name = If(String.IsNullOrEmpty(inst.DisplayName), inst.InstanceId, inst.DisplayName)
                _visibleInstancesList.Items.Add(New ListViewItem(name))
            Next
        End Sub

        Private Sub RefreshRoleMap(guildId As String)
            _roleMapList.Items.Clear()
            Dim rows As New List(Of DiscordRoleMappingEntity)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    ' Guild-default rows only (PanelId = ""). This is
                    ' exactly what slash commands resolve against —
                    ' per-panel overrides (other PanelId values) gate
                    ' panel buttons, never commands (Decision #3).
                    rows = db.DiscordRoleMappings.
                        Where(Function(m) m.GuildId = guildId AndAlso m.PanelId = "").
                        OrderBy(Function(m) m.RoleName).
                        ToList()
                End Using
            Catch
                ' DB read failure (most likely migrations not run) —
                ' fall through to the empty-state row below.
            End Try

            If rows.Count = 0 Then
                Dim row As New ListViewItem("(no elevations — only Everyone-tier commands like /help are usable)")
                row.ForeColor = SystemColors.GrayText
                _roleMapList.Items.Add(row)
                Return
            End If

            For Each m In rows
                Dim item As New ListViewItem(If(m.RoleName, "(unknown role)"))
                item.SubItems.Add(PermissionLabel(CType(m.Permission, CommandPermission)))
                _roleMapList.Items.Add(item)
            Next
        End Sub

        ' ============================================================
        '  Helpers
        ' ============================================================

        ''' <summary>
        ''' Friendly tier label for the commands list and the role
        ''' map. Distinct from the role forms' FormatPermission
        ''' (which appends "(no elevation)" to Everyone) — here a
        ''' bare tier name reads better in a "Requires" column.
        ''' </summary>
        Private Shared Function PermissionLabel(perm As CommandPermission) As String
            Select Case perm
                Case CommandPermission.Everyone
                    Return "Everyone"
                Case CommandPermission.ServerOperator
                    Return "Server Operator"
                Case CommandPermission.Administrator
                    Return "Administrator"
                Case Else
                    Return perm.ToString()
            End Select
        End Function

        Private Class GuildItem
            Public ReadOnly GuildId As String
            Public ReadOnly DisplayName As String
            Public Sub New(guildId As String, displayName As String)
                Me.GuildId = guildId
                Me.DisplayName = displayName
            End Sub
            Public Overrides Function ToString() As String
                Return DisplayName
            End Function
        End Class

    End Class

End Namespace
