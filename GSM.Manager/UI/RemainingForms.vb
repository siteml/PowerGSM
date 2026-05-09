Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Plugin
Imports GSM.Automation

' ============================================================
'  Supporting UI forms
' ============================================================

Namespace GSM.Manager.UI

    ' ============================================================
    '  PluginStatusForm — shows loaded plugins and compilation errors
    ' ============================================================

    Public Class PluginStatusForm
        Inherits Form

        Private _pluginListView As ListView
        Private _reloadButton As Button
        Private _errorTextBox As TextBox

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            RefreshPluginList()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Plugin Status"
            Me.Size = New Size(700, 500)
            Me.StartPosition = FormStartPosition.CenterParent

            _reloadButton = New Button()
            _reloadButton.Text = "Reload Plugins"
            _reloadButton.Size = New Size(130, 32)
            _reloadButton.Location = New Point(20, 15)
            AddHandler _reloadButton.Click, AddressOf OnReload
            Me.Controls.Add(_reloadButton)

            _pluginListView = New ListView()
            _pluginListView.View = View.Details
            _pluginListView.FullRowSelect = True
            _pluginListView.GridLines = True
            _pluginListView.Location = New Point(20, 55)
            _pluginListView.Size = New Size(640, 200)
            _pluginListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                      AnchorStyles.Right
            _pluginListView.Columns.Add("Game ID", 150)
            _pluginListView.Columns.Add("Display Name", 200)
            _pluginListView.Columns.Add("Status", 100)
            _pluginListView.Columns.Add("Contracts", 75)
            _pluginListView.Columns.Add("Install Methods", 180)
            Me.Controls.Add(_pluginListView)

            Dim errLabel As New Label()
            errLabel.Text = "Compilation Errors"
            errLabel.Font = New Font("Segoe UI", 10, FontStyle.Bold)
            errLabel.AutoSize = True
            errLabel.Location = New Point(20, 265)
            Me.Controls.Add(errLabel)

            _errorTextBox = New TextBox()
            _errorTextBox.Multiline = True
            _errorTextBox.ReadOnly = True
            _errorTextBox.ScrollBars = ScrollBars.Both
            _errorTextBox.Font = New Font("Consolas", 9)
            _errorTextBox.Location = New Point(20, 290)
            _errorTextBox.Size = New Size(640, 150)
            _errorTextBox.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                    AnchorStyles.Right Or AnchorStyles.Bottom
            Me.Controls.Add(_errorTextBox)
        End Sub

        Private Sub RefreshPluginList()
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            Dim runningContracts = NodeApiContract.ContractsVersion

            _pluginListView.Items.Clear()
            For Each gamePlugin In registry.GetAllPlugins()
                Dim item As New ListViewItem(gamePlugin.GameId)
                item.SubItems.Add(gamePlugin.DisplayName)
                item.SubItems.Add("Loaded")

                ' Phase 5f-3 — declared contracts version. Renders
                ' as a plain integer when matched, "v1 (old)" when
                ' the plugin targets an older version than the
                ' running manager so the user can spot a stale
                ' plugin at a glance, and "—" when the registry
                ' didn't record one (only happens if
                ' GetDeclaredContractsVersion is called for a plugin
                ' that wasn't in the last reload, which shouldn't
                ' occur via this UI path but is handled defensively).
                Dim declared = registry.GetDeclaredContractsVersion(gamePlugin.GameId)
                Dim contractsCell As String
                If declared.HasValue Then
                    If declared.Value < runningContracts Then
                        contractsCell = $"v{declared.Value} (old)"
                    Else
                        contractsCell = $"v{declared.Value}"
                    End If
                Else
                    contractsCell = "—"
                End If
                item.SubItems.Add(contractsCell)

                Dim methods = gamePlugin.GetSupportedInstallMethods()
                item.SubItems.Add(String.Join(", ", methods.Select(Function(m) m.ToString())))
                _pluginListView.Items.Add(item)
            Next
        End Sub

        Private Sub OnReload(sender As Object, e As EventArgs)
            Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
            If registry Is Nothing Then Return

            Dim orphanDetector = ManagerProgram.Services.GetService(Of PluginOrphanDetector)()
            Dim summary = registry.ReloadAll(orphanDetector)

            RefreshPluginList()

            ' Show errors
            _errorTextBox.Clear()
            If summary.CompilationErrors.Count > 0 Then
                For Each compErr In summary.CompilationErrors
                    _errorTextBox.AppendText(
                        $"{compErr.FileName}({compErr.Line},{compErr.Column}): {compErr.ErrorCode} {compErr.Message}{vbCrLf}")
                Next
            Else
                _errorTextBox.Text = "No compilation errors."
            End If

            ' Show summary
            Dim msg = $"Loaded: {summary.LoadedPlugins.Count}, " &
                      $"Added: {summary.AddedGameIds.Count}, " &
                      $"Removed: {summary.RemovedGameIds.Count}, " &
                      $"Errors: {summary.CompilationErrors.Count}"
            If summary.OrphanedInstallationIds.Count > 0 Then
                msg &= $", Orphaned installations: {summary.OrphanedInstallationIds.Count}"
            End If

            MessageBox.Show(msg, "Reload Complete",
                          MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

    End Class

    ' ============================================================
    '  SteamCredentialsForm — manage Steam login credentials
    ' ============================================================

    Public Class SteamCredentialsForm
        Inherits Form

        Private _credListView As ListView
        Private _addButton As Button
        Private _deleteButton As Button

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            RefreshList()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Steam Credentials"
            Me.Size = New Size(550, 400)
            Me.StartPosition = FormStartPosition.CenterParent

            _addButton = New Button()
            _addButton.Text = "Add"
            _addButton.Size = New Size(80, 30)
            _addButton.Location = New Point(20, 15)
            AddHandler _addButton.Click, AddressOf OnAdd
            Me.Controls.Add(_addButton)

            _deleteButton = New Button()
            _deleteButton.Text = "Delete"
            _deleteButton.Size = New Size(80, 30)
            _deleteButton.Location = New Point(110, 15)
            AddHandler _deleteButton.Click, AddressOf OnDelete
            Me.Controls.Add(_deleteButton)

            _credListView = New ListView()
            _credListView.View = View.Details
            _credListView.FullRowSelect = True
            _credListView.GridLines = True
            _credListView.Location = New Point(20, 55)
            _credListView.Size = New Size(490, 290)
            _credListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                    AnchorStyles.Right Or AnchorStyles.Bottom
            _credListView.Columns.Add("Name", 180)
            _credListView.Columns.Add("Username", 180)
            _credListView.Columns.Add("Type", 100)
            Me.Controls.Add(_credListView)
        End Sub

        Private Sub RefreshList()
            _credListView.Items.Clear()
            Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
            If credService Is Nothing Then Return

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each entity In credService.ListSteamCredentials(db)
                    Dim item As New ListViewItem(entity.DisplayName)
                    item.SubItems.Add(entity.Username)
                    item.SubItems.Add(If(entity.IsAnonymous, "Anonymous", "Login"))
                    item.Tag = entity.CredentialId
                    _credListView.Items.Add(item)
                Next
            End Using
        End Sub

        Private Sub OnAdd(sender As Object, e As EventArgs)
            Using dlg As New SteamCredentialEditForm()
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshList()
                End If
            End Using
        End Sub

        Private Sub OnDelete(sender As Object, e As EventArgs)
            If _credListView.SelectedItems.Count = 0 Then Return
            Dim credId = _credListView.SelectedItems(0).Tag.ToString()
            Dim confirm = MessageBox.Show("Delete this credential?", "Confirm",
                                         MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If confirm = DialogResult.Yes Then
                Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    credService.DeleteSteamCredential(db, credId)
                End Using
                RefreshList()
            End If
        End Sub

    End Class

    ''' <summary>
    ''' Simple edit dialog for a single Steam credential.
    ''' </summary>
    Friend Class SteamCredentialEditForm
        Inherits Form

        Private _nameTextBox As TextBox
        Private _usernameTextBox As TextBox
        Private _passwordTextBox As TextBox
        Private _anonCheckBox As CheckBox

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            Me.Text = "Add Steam Credential"
            Me.Size = New Size(400, 260)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20
            AddLabel("Display Name:", 20, y) : _nameTextBox = AddTxt(140, y, 220) : y += 35
            AddLabel("Username:", 20, y) : _usernameTextBox = AddTxt(140, y, 220) : y += 35
            AddLabel("Password:", 20, y) : _passwordTextBox = AddTxt(140, y, 220)
            _passwordTextBox.UseSystemPasswordChar = True : y += 35

            _anonCheckBox = New CheckBox()
            _anonCheckBox.Text = "Anonymous login (no credentials needed)"
            _anonCheckBox.AutoSize = True
            _anonCheckBox.Location = New Point(20, y)
            AddHandler _anonCheckBox.CheckedChanged,
                Sub(s, e)
                    _usernameTextBox.Enabled = Not _anonCheckBox.Checked
                    _passwordTextBox.Enabled = Not _anonCheckBox.Checked
                End Sub
            Me.Controls.Add(_anonCheckBox)
            y += 35

            Dim saveBtn As New Button()
            saveBtn.Text = "Save" : saveBtn.Size = New Size(80, 30)
            saveBtn.Location = New Point(190, y)
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)

            Dim cancelBtn As New Button()
            cancelBtn.Text = "Cancel" : cancelBtn.Size = New Size(80, 30)
            cancelBtn.Location = New Point(280, y)
            cancelBtn.DialogResult = DialogResult.Cancel
            Me.Controls.Add(cancelBtn)

            Me.CancelButton = cancelBtn
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Name is required.") : Return
            End If

            Dim credService = ManagerProgram.Services.GetService(Of CredentialService)()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                credService.SaveSteamCredential(db,
                    Guid.NewGuid().ToString("N"),
                    _nameTextBox.Text.Trim(),
                    If(_anonCheckBox.Checked, "anonymous", _usernameTextBox.Text.Trim()),
                    If(_anonCheckBox.Checked, "", _passwordTextBox.Text),
                    _anonCheckBox.Checked)
            End Using

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Function AddLabel(text As String, x As Integer, y As Integer) As Label
            Dim lbl As New Label() With {.Text = text, .AutoSize = True, .Location = New Point(x, y + 3)}
            Me.Controls.Add(lbl) : Return lbl
        End Function

        Private Function AddTxt(x As Integer, y As Integer, w As Integer) As TextBox
            Dim txt As New TextBox() With {.Location = New Point(x, y), .Size = New Size(w, 24)}
            Me.Controls.Add(txt) : Return txt
        End Function

    End Class

    ' ============================================================
    '  AutomationRulesForm — list and manage automation rules
    ' ============================================================

    Public Class AutomationRulesForm
        Inherits Form

        Private _rulesListView As ListView
        Private _addButton As Button
        Private _editButton As Button
        Private _deleteButton As Button
        Private _fireButton As Button
        Private _upButton As Button
        Private _downButton As Button
        Private _historyListView As ListView

        ' Phase 4b-2 polish: live status updates without modal
        ' interruption. The Last Run column shows real-time
        ' execution status pulled from RuleExecutions; this
        ' timer drives periodic refreshes while activity is
        ' detected so the column updates without the user
        ' clicking anything.
        Private _refreshTimer As Timer

        ' Track the last manual fire so we keep the timer
        ' active for ~30s afterwards even if no execution row
        ' is currently "running" (the engine may not have
        ' written the row yet, or the rule may complete in
        ' under one tick).
        Private _lastManualFireUtc As DateTime?

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            RefreshRules()
            ' Start the refresh timer disabled — RefreshRules
            ' enables it when it sees something running. Avoids
            ' burning a DB query every 3s on an idle form.
            _refreshTimer = New Timer() With {.Interval = 3000}
            AddHandler _refreshTimer.Tick, AddressOf OnRefreshTick
            AddHandler Me.FormClosed, Sub(s, e) _refreshTimer.Stop()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Automation Rules"
            ' Form widened 800 → 1080 in Phase 4b-2 to fit the
            ' humanised trigger / action columns + the Last Run
            ' column added in the rules listview redesign.
            Me.Size = New Size(1080, 600)
            Me.StartPosition = FormStartPosition.CenterParent

            ' Buttons
            _addButton = New Button() With {.Text = "Add Rule", .Size = New Size(100, 30), .Location = New Point(20, 15)}
            AddHandler _addButton.Click, AddressOf OnAdd
            Me.Controls.Add(_addButton)

            _editButton = New Button() With {.Text = "Edit", .Size = New Size(80, 30), .Location = New Point(130, 15)}
            AddHandler _editButton.Click, AddressOf OnEdit
            Me.Controls.Add(_editButton)

            _deleteButton = New Button() With {.Text = "Delete", .Size = New Size(80, 30), .Location = New Point(220, 15)}
            AddHandler _deleteButton.Click, AddressOf OnDelete
            Me.Controls.Add(_deleteButton)

            _fireButton = New Button() With {.Text = "Fire Now", .Size = New Size(90, 30), .Location = New Point(310, 15)}
            AddHandler _fireButton.Click, AddressOf OnFire
            Me.Controls.Add(_fireButton)

            ' Up / Down reorder buttons. SortOrder is purely a
            ' display preference — it has no effect on rule firing
            ' semantics — so no engine reload is needed after a
            ' reorder. See AutomationRuleEntity.SortOrder for the
            ' rationale.
            _upButton = New Button() With {.Text = "▲", .Size = New Size(40, 30), .Location = New Point(410, 15)}
            AddHandler _upButton.Click, Sub(s, ev) OnReorder(-1)
            Me.Controls.Add(_upButton)

            _downButton = New Button() With {.Text = "▼", .Size = New Size(40, 30), .Location = New Point(455, 15)}
            AddHandler _downButton.Click, Sub(s, ev) OnReorder(1)
            Me.Controls.Add(_downButton)

            ' Rules list — Phase 4b-2 redesign:
            '   - Target column shows display name (was raw ID)
            '   - Trigger column shows human-readable description
            '     (was raw JSON, useless to read)
            '   - Added Game filter and Action columns since users
            '     authoring rules across multiple games / actions
            '     want them visible at-a-glance
            '   - SubItem.Tag holds the original ID/JSON for the
            '     few code paths that still need them (delete,
            '     edit), keeping behaviour while improving display
            _rulesListView = New ListView()
            _rulesListView.View = View.Details
            _rulesListView.FullRowSelect = True
            _rulesListView.GridLines = True
            _rulesListView.Location = New Point(20, 55)
            _rulesListView.Size = New Size(1020, 200)
            _rulesListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
            ' Double-click a row to open its editor — the same path
            ' the Edit button takes. OnEdit already early-returns when
            ' nothing's selected, so a double-click on empty space is
            ' a harmless no-op.
            AddHandler _rulesListView.DoubleClick, AddressOf OnEdit
            _rulesListView.Columns.Add("Name", 170)
            _rulesListView.Columns.Add("Scope", 90)
            _rulesListView.Columns.Add("Target", 160)
            _rulesListView.Columns.Add("Game", 70)
            _rulesListView.Columns.Add("Enabled", 60)
            _rulesListView.Columns.Add("Trigger", 130)
            _rulesListView.Columns.Add("Action", 150)
            ' Last Run — Phase 4b-2 polish: shows real-time
            ' execution status ("Running...", "Ran 2m ago",
            ' "Skipped 5m ago", etc.) instead of forcing the
            ' user to scan history rows for what just happened.
            _rulesListView.Columns.Add("Last Run", 140)
            Me.Controls.Add(_rulesListView)

            ' History
            Dim histLabel As New Label() With {
                .Text = "Execution History",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(20, 265)}
            Me.Controls.Add(histLabel)

            _historyListView = New ListView()
            _historyListView.View = View.Details
            _historyListView.FullRowSelect = True
            _historyListView.GridLines = True
            _historyListView.Location = New Point(20, 290)
            _historyListView.Size = New Size(1020, 250)
            _historyListView.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                                       AnchorStyles.Right Or AnchorStyles.Bottom
            _historyListView.Columns.Add("Time", 150)
            _historyListView.Columns.Add("Rule", 170)
            _historyListView.Columns.Add("Trigger", 100)
            _historyListView.Columns.Add("Result", 100)
            _historyListView.Columns.Add("Details", 360)
            Me.Controls.Add(_historyListView)
        End Sub

        Private Sub RefreshRules()
            _rulesListView.Items.Clear()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()

                ' Pre-load lookup dictionaries once per refresh.
                ' Dictionary-per-id avoids N+1 lookups when there
                ' are many rules. The dictionaries get GC'd as soon
                ' as the using block exits. We stay consistent with
                ' the rest of this file by NOT importing
                ' Microsoft.EntityFrameworkCore here — AsNoTracking
                ' would be a perf optimization but the form is
                ' short-lived and the file's existing style uses
                ' plain ToList() throughout.
                Dim instanceById = db.Instances.
                    ToDictionary(Function(i) i.InstanceId, Function(i) i)
                Dim installById = db.Installations.
                    ToDictionary(Function(i) i.InstallationId, Function(i) i)
                Dim nodeById = db.Nodes.
                    ToDictionary(Function(n) n.NodeId, Function(n) n)
                ' Notification destinations — included so the
                ' Details column substitutes destination IDs in
                ' messages like "Notification sent to {DestId}"
                ' produced by NotifyAction's success result.
                Dim destinationById = db.NotificationDestinations.
                    ToDictionary(Function(d) d.DestinationId, Function(d) d)

                ' Latest execution per rule. Pull all rows once,
                ' group in memory — simpler than a correlated
                ' subquery and the row count is bounded by the 50
                ' we display in history (the engine prunes older).
                ' Status snapshot built here drives both the
                ' Last Run column and the timer-keep-alive logic.
                Dim allExecs = db.RuleExecutions.
                    OrderByDescending(Function(ex) ex.StartedAtUtc).
                    Take(500).
                    ToList()
                Dim latestByRule As New Dictionary(Of String, RuleExecutionEntity)
                For Each exec In allExecs
                    If Not latestByRule.ContainsKey(exec.RuleId) Then
                        latestByRule(exec.RuleId) = exec
                    End If
                Next

                ' Build a rule-id → rule-name map from the same
                ' query so the history listview below can render
                ' rule names without re-querying.
                Dim ruleNameById As New Dictionary(Of String, String)

                Dim hasActiveExecution As Boolean = False
                Dim now = DateTime.UtcNow

                For Each entity In db.AutomationRules.
                        OrderBy(Function(r) r.SortOrder).
                        ThenBy(Function(r) r.CreatedUtc).
                        ToList()
                    Dim item As New ListViewItem(entity.RuleName)
                    item.SubItems.Add(If(entity.ScopeKind, ""))
                    item.SubItems.Add(FormatTargetForRule(entity, instanceById, installById, nodeById))
                    item.SubItems.Add(ResolveRuleGame(entity, instanceById, installById))
                    item.SubItems.Add(If(entity.IsEnabled, "Yes", "No"))
                    item.SubItems.Add(FormatTriggerJson(entity.TriggerJson))
                    item.SubItems.Add(FormatActionJson(entity.ActionJson))

                    ' Last Run column — status string + colour.
                    ' Cache the per-rule running flag so we know
                    ' whether to keep the timer ticking.
                    Dim latest As RuleExecutionEntity = Nothing
                    latestByRule.TryGetValue(entity.RuleId, latest)
                    Dim status = FormatLastRunStatus(latest, now)
                    Dim lastRunSubItem = item.SubItems.Add(status.Text)
                    lastRunSubItem.ForeColor = status.Color
                    If status.IsRunning Then hasActiveExecution = True

                    item.Tag = entity.RuleId
                    _rulesListView.Items.Add(item)
                    ruleNameById(entity.RuleId) = entity.RuleName
                Next

                ' Load recent executions — rule column shows the
                ' looked-up display name with a fallback to the
                ' raw ID for executions whose rule was deleted.
                _historyListView.Items.Clear()
                Dim recentExecs = allExecs.Take(50).ToList()
                For Each exec In recentExecs
                    Dim item As New ListViewItem(exec.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                    Dim ruleDisplay As String = Nothing
                    If Not ruleNameById.TryGetValue(exec.RuleId, ruleDisplay) Then
                        ' Rule was deleted — show truncated ID so
                        ' it's at least visually distinguishable
                        ' from active rules. Full ID is rarely
                        ' useful here.
                        ruleDisplay = $"(deleted: {Truncate(exec.RuleId, 12)})"
                    End If
                    item.SubItems.Add(ruleDisplay)
                    item.SubItems.Add(If(exec.TriggerReason, ""))
                    item.SubItems.Add(If(exec.WasSkipped, "Skipped", "Executed"))
                    ' Pass the lookup dicts so embedded IDs in the
                    ' details message get substituted with display
                    ' names. "Notification sent to 84664..." →
                    ' "Notification sent to PowerGSM #test-...".
                    item.SubItems.Add(FormatExecutionDetails(exec, instanceById, installById, nodeById, destinationById))
                    _historyListView.Items.Add(item)
                Next

                ' Timer logic: keep the timer alive while:
                '   - any rule has a currently-running execution, OR
                '   - the user manually fired a rule in the last 30s
                '     (covers the brief window before the engine
                '     writes the execution row, and lets the user
                '     watch the row land + complete without losing
                '     focus).
                Dim shouldKeepTicking = hasActiveExecution
                If _lastManualFireUtc.HasValue AndAlso
                   (now - _lastManualFireUtc.Value).TotalSeconds < 30 Then
                    shouldKeepTicking = True
                End If

                If _refreshTimer IsNot Nothing Then
                    If shouldKeepTicking AndAlso Not _refreshTimer.Enabled Then
                        _refreshTimer.Start()
                    ElseIf Not shouldKeepTicking AndAlso _refreshTimer.Enabled Then
                        _refreshTimer.Stop()
                    End If
                End If
            End Using
        End Sub

        ''' <summary>
        ''' Refresh tick — just re-queries. RefreshRules itself
        ''' decides whether to keep the timer running, so this
        ''' handler stays trivial.
        ''' </summary>
        Private Sub OnRefreshTick(sender As Object, e As EventArgs)
            Try
                ' Preserve selection across refresh so users
                ' watching a specific rule's status don't lose
                ' their selection every 3 seconds.
                Dim selectedRuleId As String = Nothing
                If _rulesListView.SelectedItems.Count > 0 Then
                    selectedRuleId = TryCast(_rulesListView.SelectedItems(0).Tag, String)
                End If

                RefreshRules()

                If Not String.IsNullOrEmpty(selectedRuleId) Then
                    For Each it As ListViewItem In _rulesListView.Items
                        If String.Equals(TryCast(it.Tag, String),
                                         selectedRuleId, StringComparison.Ordinal) Then
                            it.Selected = True
                            Exit For
                        End If
                    Next
                End If
            Catch
                ' Refresh failures (DB locked etc.) are non-fatal
                ' — the next tick will retry.
            End Try
        End Sub

        ''' <summary>
        ''' Build a Last Run status descriptor from the latest
        ''' execution row. Returns a tuple-style result with the
        ''' display text, the colour to use, and a flag indicating
        ''' "this rule is currently running" so the caller can
        ''' decide whether to keep the refresh timer ticking.
        ''' </summary>
        Private Shared Function FormatLastRunStatus(
                latest As RuleExecutionEntity,
                nowUtc As DateTime) As LastRunStatus

            If latest Is Nothing Then
                Return New LastRunStatus With {
                    .Text = "—",
                    .Color = Color.FromArgb(140, 140, 140),
                    .IsRunning = False}
            End If

            ' Running: completed timestamp not yet set, and not
            ' marked as skipped (skips complete instantly with
            ' WasSkipped=true and CompletedAtUtc set together).
            If Not latest.CompletedAtUtc.HasValue AndAlso Not latest.WasSkipped Then
                Dim runFor = nowUtc - latest.StartedAtUtc
                Return New LastRunStatus With {
                    .Text = $"Running... ({FormatBriefDuration(runFor)})",
                    .Color = Color.FromArgb(180, 100, 20),
                    .IsRunning = True}
            End If

            Dim age = nowUtc - latest.StartedAtUtc
            Dim ageStr = FormatBriefAgo(age)

            If latest.WasSkipped Then
                Return New LastRunStatus With {
                    .Text = $"Skipped {ageStr}",
                    .Color = Color.FromArgb(120, 120, 120),
                    .IsRunning = False}
            End If

            ' Completed — success / failure derived from the
            ' ActionResult JSON's Success boolean. Fallback to
            ' "Ran" if parse fails (don't lie about success).
            Dim succeeded As Boolean? = Nothing
            If Not String.IsNullOrEmpty(latest.ActionResultJson) Then
                Try
                    Dim parsed = JsonSerializer.Deserialize(Of ActionResult)(latest.ActionResultJson)
                    If parsed IsNot Nothing Then succeeded = parsed.Success
                Catch
                End Try
            End If

            If succeeded.HasValue AndAlso Not succeeded.Value Then
                Return New LastRunStatus With {
                    .Text = $"Failed {ageStr}",
                    .Color = Color.FromArgb(180, 40, 40),
                    .IsRunning = False}
            End If

            ' Either Success=true OR couldn't determine — either
            ' way render as a successful-ish "Ran". Green is
            ' loud, prefer a calm dark green.
            Return New LastRunStatus With {
                .Text = $"Ran {ageStr}",
                .Color = Color.FromArgb(40, 120, 40),
                .IsRunning = False}
        End Function

        ''' <summary>
        ''' Compact "how long ago" string for the Last Run column.
        ''' Returns "just now" for < 5s, "Ns ago", "Nm ago",
        ''' "Nh ago", or "Nd ago" for older. Long output (full
        ''' timestamp) handled by the history list — this is for
        ''' the at-a-glance column only.
        ''' </summary>
        Private Shared Function FormatBriefAgo(span As TimeSpan) As String
            If span.TotalSeconds < 5 Then Return "just now"
            If span.TotalSeconds < 60 Then Return $"{CInt(span.TotalSeconds)}s ago"
            If span.TotalMinutes < 60 Then Return $"{CInt(Math.Floor(span.TotalMinutes))}m ago"
            If span.TotalHours < 24 Then Return $"{CInt(Math.Floor(span.TotalHours))}h ago"
            Return $"{CInt(Math.Floor(span.TotalDays))}d ago"
        End Function

        ''' <summary>
        ''' Same as FormatBriefAgo but without the "ago" suffix
        ''' — used to render duration of a still-running execution
        ''' ("Running... (12s)").
        ''' </summary>
        Private Shared Function FormatBriefDuration(span As TimeSpan) As String
            If span.TotalSeconds < 1 Then Return "<1s"
            If span.TotalSeconds < 60 Then Return $"{CInt(span.TotalSeconds)}s"
            If span.TotalMinutes < 60 Then Return $"{CInt(Math.Floor(span.TotalMinutes))}m"
            Return $"{CInt(Math.Floor(span.TotalHours))}h"
        End Function

        ''' <summary>
        ''' Helper struct for FormatLastRunStatus's three return
        ''' values. Class rather than structure because VB.Net's
        ''' structure semantics around object initializers are
        ''' fiddly and the per-row allocation is irrelevant here.
        ''' </summary>
        Private Class LastRunStatus
            Public Property Text As String
            Public Property Color As Color
            Public Property IsRunning As Boolean
        End Class

        ' ============================================================
        '  Phase 4b-2: human-readable formatters for the rules
        '  listview. Failures everywhere fall back to a sensible
        '  string rather than throwing — a malformed rule shouldn't
        '  crash the form.
        ' ============================================================

        ''' <summary>
        ''' Resolve the effective game for the Game column. Logic:
        '''   - Instance scope     → target instance's GameId
        '''   - Installation scope → target installation's GameId
        '''   - Multi-instance     → GameFilter if explicitly set,
        '''                          else "common game" if all
        '''                          covered instances share one,
        '''                          else "—" (genuinely mixed)
        '''
        ''' The implicit-single-game resolution exists so that
        ''' an InstanceSet of three lastoasis realms with no
        ''' GameFilter still shows "lastoasis" rather than "—".
        ''' Same goes for AllInstances when the deployment only
        ''' runs one game, and Node when the node only hosts
        ''' installations of one game.
        '''
        ''' Mixed sets fall back to "—". "(mixed)" was considered
        ''' but rejected for column-width reasons — the dash is
        ''' visually clear in context (multi-instance scope +
        ''' dash = "this spans games").
        ''' </summary>
        Private Shared Function ResolveRuleGame(
                entity As AutomationRuleEntity,
                instanceById As Dictionary(Of String, InstanceEntity),
                installById As Dictionary(Of String, InstallationEntity)) As String
            Dim scope As RuleScope
            If Not [Enum].TryParse(entity.ScopeKind, True, scope) Then
                Return If(String.IsNullOrEmpty(entity.GameFilter), "—", entity.GameFilter)
            End If

            Select Case scope
                Case RuleScope.Instance
                    Dim inst As InstanceEntity = Nothing
                    If instanceById.TryGetValue(If(entity.TargetId, ""), inst) Then
                        Return If(String.IsNullOrEmpty(inst.GameId), "—", inst.GameId)
                    End If
                    Return "—"

                Case RuleScope.Installation
                    Dim ins As InstallationEntity = Nothing
                    If installById.TryGetValue(If(entity.TargetId, ""), ins) Then
                        Return If(String.IsNullOrEmpty(ins.GameId), "—", ins.GameId)
                    End If
                    Return "—"

                Case RuleScope.InstanceSet
                    ' Explicit GameFilter wins. Otherwise check
                    ' whether all tagged instances share one game.
                    If Not String.IsNullOrEmpty(entity.GameFilter) Then Return entity.GameFilter
                    Return GetSingleGameOrDash(
                        instanceById.Values.Where(
                            Function(i) String.Equals(i.InstanceSetTag,
                                                       entity.TargetId,
                                                       StringComparison.Ordinal)))

                Case RuleScope.Node
                    If Not String.IsNullOrEmpty(entity.GameFilter) Then Return entity.GameFilter
                    ' Build the set of installation IDs hosted on
                    ' this node, then look at instances of those
                    ' installations. Two-hop because instances
                    ' don't have a NodeId directly — they get it
                    ' through their installation.
                    Dim nodeInstallIds = installById.Values.
                        Where(Function(ins) String.Equals(ins.NodeId,
                                                           entity.TargetId,
                                                           StringComparison.Ordinal)).
                        Select(Function(ins) ins.InstallationId).
                        ToHashSet()
                    Return GetSingleGameOrDash(
                        instanceById.Values.Where(
                            Function(i) nodeInstallIds.Contains(i.InstallationId)))

                Case RuleScope.AllInstances
                    If Not String.IsNullOrEmpty(entity.GameFilter) Then Return entity.GameFilter
                    Return GetSingleGameOrDash(instanceById.Values)

                Case Else
                    Return If(String.IsNullOrEmpty(entity.GameFilter), "—", entity.GameFilter)
            End Select
        End Function

        ''' <summary>
        ''' Returns the single distinct GameId across the candidate
        ''' instances, or "—" if there are zero or multiple distinct
        ''' games. Empty/null GameIds are excluded from the
        ''' distinct count so a single half-configured row doesn't
        ''' make a single-game set look mixed.
        ''' </summary>
        Private Shared Function GetSingleGameOrDash(
                candidates As IEnumerable(Of InstanceEntity)) As String
            If candidates Is Nothing Then Return "—"
            Dim distinctGames = candidates.
                Where(Function(i) Not String.IsNullOrEmpty(i.GameId)).
                Select(Function(i) i.GameId).
                Distinct().
                ToList()
            If distinctGames.Count = 1 Then Return distinctGames(0)
            Return "—"
        End Function

        ''' <summary>
        ''' Resolve the rule's TargetId to a display name based on
        ''' its scope. AllInstances has no target. InstanceSet uses
        ''' the raw tag string. The other three look up their
        ''' respective entities and fall back to the raw ID with a
        ''' "(deleted)" suffix when the lookup misses.
        ''' </summary>
        Private Shared Function FormatTargetForRule(
                entity As AutomationRuleEntity,
                instanceById As Dictionary(Of String, InstanceEntity),
                installById As Dictionary(Of String, InstallationEntity),
                nodeById As Dictionary(Of String, NodeEntity)) As String

            Dim scope As RuleScope
            If Not [Enum].TryParse(entity.ScopeKind, True, scope) Then
                Return If(entity.TargetId, "")
            End If

            Select Case scope
                Case RuleScope.AllInstances
                    Return "—"
                Case RuleScope.InstanceSet
                    Return If(entity.TargetId, "")
                Case RuleScope.Instance
                    Dim inst As InstanceEntity = Nothing
                    If instanceById.TryGetValue(If(entity.TargetId, ""), inst) Then
                        Return inst.DisplayName
                    End If
                Case RuleScope.Installation
                    Dim ins As InstallationEntity = Nothing
                    If installById.TryGetValue(If(entity.TargetId, ""), ins) Then
                        Return ins.DisplayName
                    End If
                Case RuleScope.Node
                    Dim n As NodeEntity = Nothing
                    If nodeById.TryGetValue(If(entity.TargetId, ""), n) Then
                        Return n.DisplayName
                    End If
            End Select

            ' Lookup miss — return short ID with a deleted hint so
            ' the user sees that something used to be there.
            Return $"(deleted: {Truncate(If(entity.TargetId, ""), 12)})"
        End Function

        ''' <summary>
        ''' Convert the trigger JSON into a one-liner. Rather than
        ''' deserialising via AutomationRuleSerializer (which would
        ''' mean instantiating concrete trigger types and depending
        ''' on the full Automation namespace at the formatter level),
        ''' read the JSON directly via JsonDocument and pick out
        ''' the discriminator + relevant fields. Cheap and safe.
        ''' </summary>
        Private Shared Function FormatTriggerJson(json As String) As String
            If String.IsNullOrEmpty(json) Then Return ""
            Try
                Using doc = JsonDocument.Parse(json)
                    Dim root = doc.RootElement
                    Dim triggerId As String = ""
                    Dim idElement As JsonElement
                    If root.TryGetProperty("triggerId", idElement) Then
                        triggerId = idElement.GetString()
                    End If

                    Select Case triggerId
                        Case "schedule"
                            Dim cronElement As JsonElement
                            If root.TryGetProperty("cronExpression", cronElement) Then
                                Return FormatCron(cronElement.GetString())
                            End If
                            Return "Scheduled"
                        Case "state_change"
                            Dim fromState As String = ""
                            Dim toState As String = ""
                            Dim el As JsonElement
                            If root.TryGetProperty("fromState", el) AndAlso
                               el.ValueKind <> JsonValueKind.Null Then
                                fromState = el.GetString()
                            End If
                            If root.TryGetProperty("toState", el) AndAlso
                               el.ValueKind <> JsonValueKind.Null Then
                                toState = el.GetString()
                            End If
                            If String.IsNullOrEmpty(fromState) AndAlso String.IsNullOrEmpty(toState) Then
                                Return "On state change"
                            End If
                            Dim fromPart = If(String.IsNullOrEmpty(fromState), "any", fromState)
                            Dim toPart = If(String.IsNullOrEmpty(toState), "any", toState)
                            Return $"On {fromPart} → {toPart}"
                        Case "version_mismatch"
                            Return "On update available"
                        Case "manual"
                            Return "Manual fire only"
                        Case Else
                            Return If(String.IsNullOrEmpty(triggerId), "(unknown)", triggerId)
                    End Select
                End Using
            Catch
                Return "(invalid trigger JSON)"
            End Try
        End Function

        ''' <summary>
        ''' Render a cron expression in a human-friendly way for
        ''' the common patterns we generate ourselves: "0 H * * *"
        ''' → "Daily at HH:00", "M H * * *" → "Daily at HH:MM",
        ''' "0 */N * * *" → "Every N hours". Anything else falls
        ''' back to the raw cron prefixed with "Cron:" so users
        ''' see what they typed.
        ''' </summary>
        Private Shared Function FormatCron(cron As String) As String
            If String.IsNullOrWhiteSpace(cron) Then Return "Scheduled"
            Dim parts = cron.Trim().Split({" "c, ChrW(9)},
                                            StringSplitOptions.RemoveEmptyEntries)
            If parts.Length < 5 Then Return $"Cron: {cron}"

            Dim minute = parts(0)
            Dim hour = parts(1)
            Dim dom = parts(2)
            Dim mon = parts(3)
            Dim dow = parts(4)

            ' Daily check: minute and hour are integers, dom/mon/dow all *
            If dom = "*" AndAlso mon = "*" AndAlso dow = "*" Then
                Dim m, h As Integer
                If Integer.TryParse(minute, m) AndAlso Integer.TryParse(hour, h) Then
                    Return $"Daily at {h:D2}:{m:D2}"
                End If
                ' Interval: minute=0, hour="*/N"
                If minute = "0" AndAlso hour.StartsWith("*/") Then
                    Dim n As Integer
                    If Integer.TryParse(hour.Substring(2), n) Then
                        Return $"Every {n} hour{If(n = 1, "", "s")}"
                    End If
                End If
            End If

            ' Anything else — hand back the raw cron, prefixed.
            Return $"Cron: {cron}"
        End Function

        ''' <summary>
        ''' Convert the action JSON into a one-liner. Same approach
        ''' as FormatTriggerJson — read the actionId discriminator
        ''' and map to a friendly label. SequenceAction shows the
        ''' step count to give users a hint of complexity.
        ''' </summary>
        Private Shared Function FormatActionJson(json As String) As String
            If String.IsNullOrEmpty(json) Then Return ""
            Try
                Using doc = JsonDocument.Parse(json)
                    Dim root = doc.RootElement
                    Dim actionId As String = ""
                    Dim idElement As JsonElement
                    If root.TryGetProperty("actionId", idElement) Then
                        actionId = idElement.GetString()
                    End If

                    Select Case actionId
                        Case "coordinated_restart" : Return "Coordinated Restart"
                        Case "start_instance" : Return "Start instance"
                        Case "stop_instance" : Return "Stop instance"
                        Case "restart_instance" : Return "Restart instance"
                        Case "start_all_instances" : Return "Start all instances"
                        Case "stop_all_instances" : Return "Stop all instances"
                        Case "update_installation" : Return "Update installation"
                        Case "send_rcon" : Return "Send RCON command"
                        Case "notify" : Return "Send notification"
                        Case "wait" : Return "Wait"
                        Case "wait_for_ready" : Return "Wait for ready signal"
                        Case "sequence"
                            ' Show step count for sequences — it's
                            ' the most useful single signal of
                            ' "how complicated is this rule?".
                            Dim stepsElement As JsonElement
                            If root.TryGetProperty("steps", stepsElement) AndAlso
                               stepsElement.ValueKind = JsonValueKind.Array Then
                                Dim count = stepsElement.GetArrayLength()
                                Return $"Sequence ({count} step{If(count = 1, "", "s")})"
                            End If
                            Return "Sequence"
                        Case Else
                            Return If(String.IsNullOrEmpty(actionId), "(unknown)", actionId)
                    End Select
                End Using
            Catch
                Return "(invalid action JSON)"
            End Try
        End Function

        ''' <summary>
        ''' Helper used in deletion-fallback display strings. Avoids
        ''' Substring throwing on shorter inputs.
        ''' </summary>
        Private Shared Function Truncate(s As String, n As Integer) As String
            If String.IsNullOrEmpty(s) Then Return ""
            If s.Length <= n Then Return s
            Return s.Substring(0, n)
        End Function

        Private Sub OnAdd(sender As Object, e As EventArgs)
            Using dlg As New RuleEditorForm()
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshRules()
                End If
            End Using
        End Sub

        ''' <summary>
        ''' Produce a human-readable details string for the history
        ''' row. SkipReason wins when present; otherwise deserialise
        ''' the ActionResult JSON and show its Message field so users
        ''' see "Skipped: foo is Stopped, not Running" rather than a
        ''' raw truncated JSON envelope. Falls back to raw JSON
        ''' (truncated) if the parse fails.
        '''
        ''' Phase 4b-2 polish: takes the lookup dictionaries so any
        ''' embedded GUIDs in the message text get substituted with
        ''' display names. The upstream code paths build messages
        ''' like "Skipped: {InstanceId} is Stopped, not Running" and
        ''' "Notification sent to {DestinationId}" with raw IDs;
        ''' rather than refactoring those (which would mean threading
        ''' display names through several layers), we post-process
        ''' here.
        ''' </summary>
        Private Shared Function FormatExecutionDetails(
                exec As RuleExecutionEntity,
                instanceById As Dictionary(Of String, InstanceEntity),
                installById As Dictionary(Of String, InstallationEntity),
                nodeById As Dictionary(Of String, NodeEntity),
                destinationById As Dictionary(Of String, NotificationDestinationEntity)) As String
            Dim raw As String
            If Not String.IsNullOrEmpty(exec.SkipReason) Then
                raw = exec.SkipReason
            Else
                Dim json = exec.ActionResultJson
                If String.IsNullOrEmpty(json) Then Return ""
                Try
                    ' Deserialise directly into ActionResult. Safer than
                    ' a Dictionary(Of String, Object) parse — STJ boxes
                    ' values as JsonElement there and ToString() behaviour
                    ' around quoted/unquoted strings varies by version.
                    Dim parsed = JsonSerializer.Deserialize(Of ActionResult)(json)
                    If parsed IsNot Nothing AndAlso Not String.IsNullOrEmpty(parsed.Message) Then
                        raw = parsed.Message
                    Else
                        raw = If(json.Length > 80, json.Substring(0, 80) & "...", json)
                    End If
                Catch
                    raw = If(json.Length > 80, json.Substring(0, 80) & "...", json)
                End Try
            End If
            Return SubstituteIdsWithNames(raw, instanceById, installById, nodeById, destinationById)
        End Function

        ''' <summary>
        ''' Replace 32-character hex GUIDs in a message string with
        ''' the display names of any matching entities. Strict 32-hex
        ''' match (no hyphens — our IDs use Guid.NewGuid().ToString("N")
        ''' which produces dashless 32-char hex). Looks across
        ''' instances, installations, nodes, and notification
        ''' destinations; returns the first lookup match.
        '''
        ''' Aggressive matching (e.g. partial IDs) was rejected
        ''' because it could mangle innocuous text. 32-hex is
        ''' specific enough to almost never false-positive on
        ''' real prose.
        ''' </summary>
        Private Shared Function SubstituteIdsWithNames(
                input As String,
                instanceById As Dictionary(Of String, InstanceEntity),
                installById As Dictionary(Of String, InstallationEntity),
                nodeById As Dictionary(Of String, NodeEntity),
                destinationById As Dictionary(Of String, NotificationDestinationEntity)) As String
            If String.IsNullOrEmpty(input) Then Return input
            ' Quick scan: skip the regex if the string can't possibly
            ' contain a 32-hex run.
            If input.Length < 32 Then Return input

            Return System.Text.RegularExpressions.Regex.Replace(
                input,
                "[0-9a-f]{32}",
                Function(m)
                    Dim id = m.Value
                    Dim inst As InstanceEntity = Nothing
                    If instanceById IsNot Nothing AndAlso instanceById.TryGetValue(id, inst) Then
                        Return inst.DisplayName
                    End If
                    Dim ins As InstallationEntity = Nothing
                    If installById IsNot Nothing AndAlso installById.TryGetValue(id, ins) Then
                        Return ins.DisplayName
                    End If
                    Dim n As NodeEntity = Nothing
                    If nodeById IsNot Nothing AndAlso nodeById.TryGetValue(id, n) Then
                        Return n.DisplayName
                    End If
                    Dim dest As NotificationDestinationEntity = Nothing
                    If destinationById IsNot Nothing AndAlso destinationById.TryGetValue(id, dest) Then
                        Return dest.DisplayName
                    End If
                    ' Unknown ID — leave literal so the user can
                    ' still see something. Truncating would hide
                    ' info; keeping it visible is better.
                    Return id
                End Function,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        End Function

        Private Sub OnEdit(sender As Object, e As EventArgs)
            If _rulesListView.SelectedItems.Count = 0 Then Return
            Dim ruleId = _rulesListView.SelectedItems(0).Tag.ToString()
            Using dlg As New RuleEditorForm(ruleId)
                If dlg.ShowDialog(Me) = DialogResult.OK Then
                    RefreshRules()
                End If
            End Using
        End Sub

        Private Sub OnDelete(sender As Object, e As EventArgs)
            If _rulesListView.SelectedItems.Count = 0 Then Return
            Dim ruleId = _rulesListView.SelectedItems(0).Tag.ToString()
            If MessageBox.Show("Delete this rule?", "Confirm",
                             MessageBoxButtons.YesNo) = DialogResult.Yes Then
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim entity = db.AutomationRules.Find(ruleId)
                    If entity IsNot Nothing Then
                        db.AutomationRules.Remove(entity)
                        db.SaveChanges()
                    End If
                End Using
                Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
                engine?.ReloadRules()
                RefreshRules()
            End If
        End Sub

        ''' <summary>
        ''' Move the selected rule up (-1) or down (+1) in SortOrder.
        ''' Implemented as a swap with the adjacent sibling — same
        ''' pattern as InstallationPanel.OnReorderInstance — so
        ''' SortOrder values stay consecutive without renumbering
        ''' the whole table on every move.
        '''
        ''' No AutomationEngine.ReloadRules() call here: SortOrder
        ''' is display-only. Two rules whose triggers fire at the
        ''' same instant queue based on engine internals (cron tick
        ''' order, condition evaluation order), not on this column.
        ''' </summary>
        Private Sub OnReorder(direction As Integer)
            If _rulesListView.SelectedItems.Count = 0 Then Return
            Dim selectedIdx = _rulesListView.SelectedItems(0).Index
            Dim newIdx = selectedIdx + direction
            If newIdx < 0 OrElse newIdx >= _rulesListView.Items.Count Then Return

            Dim selectedId = _rulesListView.SelectedItems(0).Tag.ToString()
            Dim swapId = _rulesListView.Items(newIdx).Tag.ToString()

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim a = db.AutomationRules.Find(selectedId)
                Dim b = db.AutomationRules.Find(swapId)
                If a Is Nothing OrElse b Is Nothing Then Return

                ' Swap SortOrder values. If they happen to be equal
                ' (legacy backfilled rows where the migration didn't
                ' assign distinct positions), nudge them apart so the
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

            ' Refresh the list and re-select the moved rule by ID
            ' so the user can keep arrow-clicking to walk it up or
            ' down without losing their place. Selection follows the
            ' rule, not the row index.
            RefreshRules()
            For Each it As ListViewItem In _rulesListView.Items
                If String.Equals(TryCast(it.Tag, String), selectedId,
                                   StringComparison.Ordinal) Then
                    it.Selected = True
                    it.EnsureVisible()
                    Exit For
                End If
            Next
        End Sub

        Private Async Sub OnFire(sender As Object, e As EventArgs)
            If _rulesListView.SelectedItems.Count = 0 Then Return
            Dim ruleId = _rulesListView.SelectedItems(0).Tag.ToString()
            Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
            If engine Is Nothing Then Return

            ' Phase 4b-2 polish: dropped the blocking MessageBox
            ' on fire. The Last Run column shows result + status
            ' live ("Running..." → "Ran 5s ago" / "Skipped 5s ago"
            ' / "Failed 5s ago") via the refresh timer, so users
            ' get richer feedback without losing focus.
            '
            ' Mark the manual-fire timestamp BEFORE awaiting the
            ' fire call so the timer-keep-alive logic in
            ' RefreshRules sees it on the immediately-following
            ' refresh. Without this, the engine could complete a
            ' fast rule before the next refresh arms the timer.
            _lastManualFireUtc = DateTime.UtcNow
            RefreshRules()  ' arms the timer

            Try
                Await engine.FireRuleManuallyAsync(ruleId)
            Catch
                ' Engine errors surface in the execution row's
                ' ActionResult; nothing to do here.
            End Try

            ' Refresh once more after the await returns so the
            ' "Ran" / "Skipped" / "Failed" status lands
            ' immediately. Subsequent timer ticks keep the age
            ' string fresh until the 30-second window closes.
            RefreshRules()
        End Sub

    End Class

    ' ============================================================
    '  SettingsForm — global preferences
    '
    '  Currently hosts chat-retention configuration plus a display
    '  of the resolved database and plugins paths (useful when
    '  troubleshooting multi-install setups or service deployments
    '  where the "current directory" isn't obvious).
    '
    '  Retention changes are picked up by ChatRetentionPruner on its
    '  next pass (runs hourly), so the UI surfaces that expectation
    '  rather than pretending changes are instant.
    ' ============================================================

    Public Class SettingsForm
        Inherits Form

        Private _retentionDaysNumeric As NumericUpDown
        Private _saveButton As Button
        Private _cancelButton As Button

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            Me.Text = "Settings"
            Me.Size = New Size(540, 360)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20

            ' ---- Data Retention ----
            Dim retentionHdr As New Label() With {
                .Text = "Data Retention",
                .Font = New Font("Segoe UI", 11, FontStyle.Bold),
                .ForeColor = Color.FromArgb(50, 50, 120),
                .AutoSize = True,
                .Location = New Point(20, y)
            }
            Me.Controls.Add(retentionHdr)
            y += 28

            Dim daysLbl As New Label() With {
                .Text = "Chat retention (days):",
                .AutoSize = True,
                .Location = New Point(20, y + 4)
            }
            Me.Controls.Add(daysLbl)

            _retentionDaysNumeric = New NumericUpDown() With {
                .Location = New Point(200, y),
                .Size = New Size(100, 24),
                .Minimum = 1,
                .Maximum = 3650,
                .Value = GsmDataExtensions.DefaultChatRetentionDays
            }
            Me.Controls.Add(_retentionDaysNumeric)
            y += 32

            Dim retentionHelp As New Label() With {
                .Text = "Chat messages older than this are deleted on the retention pruner's hourly pass." & vbCrLf &
                        "PlayerSessions and PlayerActivity are never time-pruned — they persist until the" & vbCrLf &
                        "underlying session identity goes away (e.g. a realm is reset).",
                .ForeColor = Color.DimGray,
                .Font = New Font("Segoe UI", 8.5F),
                .AutoSize = True,
                .Location = New Point(20, y)
            }
            Me.Controls.Add(retentionHelp)
            y += 60

            ' ---- Paths ----
            Dim pathsHdr As New Label() With {
                .Text = "Paths",
                .Font = New Font("Segoe UI", 11, FontStyle.Bold),
                .ForeColor = Color.FromArgb(50, 50, 120),
                .AutoSize = True,
                .Location = New Point(20, y)
            }
            Me.Controls.Add(pathsHdr)
            y += 28

            ' Full-path resolution so the user sees exactly where the
            ' files live — particularly useful when the Manager is
            ' launched from a shortcut with a different working dir.
            Dim dbPathLbl As New Label() With {
                .Text = $"Database: {ResolveFullPath("gsm.db")}",
                .AutoSize = False,
                .Size = New Size(490, 20),
                .AutoEllipsis = True,
                .Location = New Point(20, y),
                .Font = New Font("Segoe UI", 9)
            }
            Me.Controls.Add(dbPathLbl)
            y += 22

            Dim pluginPathLbl As New Label() With {
                .Text = $"Plugins directory: {ResolveFullPath("Plugins")}",
                .AutoSize = False,
                .Size = New Size(490, 20),
                .AutoEllipsis = True,
                .Location = New Point(20, y),
                .Font = New Font("Segoe UI", 9)
            }
            Me.Controls.Add(pluginPathLbl)

            ' ---- Buttons (anchored near the bottom) ----
            _saveButton = New Button() With {
                .Text = "Save",
                .Size = New Size(90, 30),
                .Location = New Point(320, 280)
            }
            AddHandler _saveButton.Click, AddressOf OnSave
            Me.Controls.Add(_saveButton)

            _cancelButton = New Button() With {
                .Text = "Cancel",
                .Size = New Size(90, 30),
                .Location = New Point(420, 280),
                .DialogResult = DialogResult.Cancel
            }
            Me.Controls.Add(_cancelButton)

            Me.AcceptButton = _saveButton
            Me.CancelButton = _cancelButton

            LoadCurrentValues()
        End Sub

        ''' <summary>
        ''' Resolve a path to absolute form for display. Wrapped in
        ''' try/catch because GetFullPath throws on malformed inputs
        ''' and we don't want a path resolution failure to break
        ''' the Settings dialog.
        ''' </summary>
        Private Shared Function ResolveFullPath(relative As String) As String
            Try
                Return IO.Path.GetFullPath(relative)
            Catch
                Return relative
            End Try
        End Function

        Private Sub LoadCurrentValues()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim days = db.GetSettingInt(
                        GsmDataExtensions.SettingKeys.ChatRetentionDays,
                        GsmDataExtensions.DefaultChatRetentionDays)
                    ' Clamp to the NumericUpDown range so a wild DB
                    ' value doesn't throw at assignment time.
                    If days < _retentionDaysNumeric.Minimum Then days = CInt(_retentionDaysNumeric.Minimum)
                    If days > _retentionDaysNumeric.Maximum Then days = CInt(_retentionDaysNumeric.Maximum)
                    _retentionDaysNumeric.Value = days
                End Using
            Catch
                ' Swallow — form already shows the default value.
            End Try
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    db.SetSetting(
                        GsmDataExtensions.SettingKeys.ChatRetentionDays,
                        CInt(_retentionDaysNumeric.Value).ToString())
                    db.SaveChanges()
                End Using
                Me.DialogResult = DialogResult.OK
                Me.Close()
            Catch ex As Exception
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Sub

    End Class

    ' ============================================================
    '  AddInstanceForm — add a new instance to an existing installation
    ' ============================================================

    Public Class AddInstanceForm
        Inherits Form

        Private ReadOnly _installationId As String
        Private _nameTextBox As TextBox
        Private _configPanel As Panel
        Private _schemaResult As SchemaFormResult

        Public Sub New(installationId As String)
            _installationId = installationId
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Add Instance"
            Me.Size = New Size(550, 450)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20

            ' Instance name
            Dim nameLbl As New Label() With {
                .Text = "Instance Name:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(nameLbl)
            _nameTextBox = New TextBox() With {
                .Location = New Point(150, y), .Size = New Size(350, 24)}
            _nameTextBox.Text = "Server 1"
            Me.Controls.Add(_nameTextBox)
            y += 40

            ' Config panel — load schema from plugin
            Dim configLabel As New Label() With {
                .Text = "Instance Configuration",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(20, y)}
            Me.Controls.Add(configLabel)
            y += 25

            _configPanel = New Panel() With {
                .Location = New Point(20, y),
                .Size = New Size(490, 250),
                .BorderStyle = BorderStyle.FixedSingle,
                .AutoScroll = True}
            Me.Controls.Add(_configPanel)

            ' Load plugin schema
            LoadPluginSchema()

            y += 260
            Dim saveBtn As New Button() With {
                .Text = "Create", .Size = New Size(100, 32),
                .Location = New Point(300, y)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)

            Dim cancelBtn As New Button() With {
                .Text = "Cancel", .Size = New Size(100, 32),
                .Location = New Point(410, y)}
            cancelBtn.DialogResult = DialogResult.Cancel
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn
        End Sub

        Private Sub LoadPluginSchema()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(_installationId)
                If installEntity Is Nothing Then Return

                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return

                Dim gamePlugin = registry.GetPlugin(installEntity.GameId)
                If gamePlugin Is Nothing Then Return

                Dim schema = gamePlugin.GetInstanceConfigSchema()

                ' Filter out RCON fields if the plugin doesn't support RCON
                If Not gamePlugin.GetRconProtocol().HasValue Then
                    schema = schema.Where(Function(f) _
                        Not String.Equals(f.Key, "RconPort", StringComparison.OrdinalIgnoreCase) AndAlso
                        Not String.Equals(f.Key, "RconPassword", StringComparison.OrdinalIgnoreCase)
                    ).ToList()
                End If

                ' Append manager-level lifecycle knobs (crash policy,
                ' restart timing, graceful shutdown) so they show up
                ' on every instance regardless of game plugin.
                schema = schema.Concat(CommonConfigFields.GetInstanceLifecycleFields()).ToList()

                ' Pre-fill port-typed fields with PortAllocator's
                ' suggestions so the user doesn't have to manually
                ' pick a non-colliding port for each new instance.
                ' SchemaFormBuilder treats the `existing` dict's
                ' values as field defaults, so this just renders
                ' the suggested numbers as the initial form values.
                ' User can edit them like any other field.
                Dim portSuggestions = PortAllocator.SuggestPortsForNewInstance(
                    gamePlugin, installEntity.NodeId, db)

                _schemaResult = SchemaFormBuilder.Build(schema, portSuggestions)
                If _schemaResult.Panel IsNot Nothing Then
                    _schemaResult.Panel.Dock = DockStyle.Fill
                    _configPanel.Controls.Add(_schemaResult.Panel)
                End If
            End Using
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Instance name is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' Hard limit check — re-evaluated at save time as the
            ' authoritative gate. The MainForm context menu greys
            ' itself out when the limit is hit, but several things
            ' can still get past that: a TOCTOU window between menu
            ' render and submit (another window adding an instance
            ' in the meantime), code paths that bypass the menu
            ' entirely, or future callers that open this form
            ' directly. Plugins that return Nothing for
            ' MaxInstancesPerInstallation skip the check.
            Using checkScope = ManagerProgram.Services.CreateScope()
                Dim checkDb = checkScope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installForLimit = checkDb.Installations.Find(_installationId)
                If installForLimit IsNot Nothing Then
                    Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                    If registry IsNot Nothing Then
                        Dim plugin As IGamePlugin = registry.GetPlugin(installForLimit.GameId)
                        If plugin IsNot Nothing AndAlso
                           plugin.MaxInstancesPerInstallation.HasValue Then
                            Dim limit = plugin.MaxInstancesPerInstallation.Value
                            Dim existing = checkDb.Instances.
                                Count(Function(i) i.InstallationId = _installationId)
                            If existing >= limit Then
                                Dim limitWord = If(limit = 1, "instance", "instances")
                                MessageBox.Show(
                                    $"{plugin.DisplayName} supports a maximum of {limit} {limitWord} per installation. " &
                                    $"This installation already has {existing}." & vbCrLf & vbCrLf &
                                    "Create a separate installation to run another server.",
                                    "Limit Reached",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information)
                                Return
                            End If
                        End If
                    End If
                End If
            End Using

            Dim configValues As New Dictionary(Of String, String)
            If _schemaResult IsNot Nothing AndAlso _schemaResult.ValueExtractor IsNot Nothing Then
                configValues = _schemaResult.ValueExtractor.Invoke()
            End If

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(_installationId)
                If installEntity Is Nothing Then Return

                ' Port conflict check before save. Conflicts on the
                ' same node WILL cause the server to fail to bind on
                ' Start, so surface them now and let the user fix or
                ' deliberately override. Warn-and-confirm rather than
                ' block: there are legitimate cases (instances that
                ' never run simultaneously, manual override) where
                ' the user knows what they're doing. The list shows
                ' EVERY collision so the user sees the full picture
                ' rather than fixing one and discovering another.
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry IsNot Nothing Then
                    Dim plugin = registry.GetPlugin(installEntity.GameId)
                    If plugin IsNot Nothing Then
                        Dim conflicts = PortAllocator.FindPortConflicts(
                            plugin, installEntity.NodeId, "", configValues, db)
                        If conflicts.Count > 0 Then
                            Dim msg = "Port conflicts detected:" & vbCrLf & vbCrLf &
                                PortAllocator.FormatConflictsForDisplay(conflicts) & vbCrLf &
                                "Conflicting ports will fail to bind when both servers run at the same time." & vbCrLf &
                                "Save anyway?"
                            Dim res = MessageBox.Show(msg, "Port Conflicts",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                            If res <> DialogResult.Yes Then Return
                        End If
                    End If
                End If

                Dim instanceEntity As New InstanceEntity With {
                    .InstanceId = Guid.NewGuid().ToString("N"),
                    .InstallationId = _installationId,
                    .GameId = installEntity.GameId,
                    .DisplayName = _nameTextBox.Text.Trim(),
                    .ConfigJson = JsonSerializer.Serialize(configValues),
                    .CreatedUtc = DateTime.UtcNow,
                    .UpdatedUtc = DateTime.UtcNow
                }
                db.Instances.Add(instanceEntity)
                db.SaveChanges()
            End Using

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

    End Class

    ' ============================================================
    '  EditInstanceForm — edit an existing instance's config,
    '  including the Restart Schedule quick-config section.
    '
    '  The Restart Schedule section materialises into an
    '  AutomationRule via RestartRuleMaterializer on save. If the
    '  existing rule has been edited in the Automation Rules
    '  window beyond what this form can express (added conditions,
    '  swapped the action, etc.), the section grays out and shows
    '  a link back to the rule editor — preserving the power
    '  user's changes rather than silently stomping them.
    ' ============================================================

    Public Class EditInstanceForm
        Inherits Form

        Private ReadOnly _instanceId As String
        Private _nameTextBox As TextBox
        Private _exeOverrideTextBox As TextBox
        Private _autoStartCheckBox As CheckBox
        Private _instanceSetCombo As ComboBox
        Private _configPanel As Panel
        Private _schemaResult As SchemaFormResult

        ' ---- Restart Schedule controls ----
        ' Everything in the normal-mode section lives inside
        ' _normalPanel so ApplyDriftState can hide it as a unit.
        ' Individual field references are kept so event handlers
        ' can tweak enabled/text state without re-finding them.
        Private _normalPanel As Panel
        Private _driftPanel As Panel
        Private _restartEnabledCheckBox As CheckBox
        Private _cronTextBox As TextBox
        Private _nextRunLabel As Label
        Private _dailyHourNumeric As NumericUpDown
        Private _setDailyButton As Button
        Private _intervalHoursNumeric As NumericUpDown
        Private _setIntervalButton As Button
        Private _staggerStepNumeric As NumericUpDown
        Private _propagateNoneRadio As RadioButton
        Private _propagateStaggerRadio As RadioButton
        Private _propagateLiteralRadio As RadioButton
        Private _enableOnAllCheckBox As CheckBox
        Private _restartHelpLabel As Label
        Private _driftWarningLabel As Label
        Private _openInAutomationButton As Button

        ' ---- Restart Schedule state ----
        ' True when the existing rule doesn't match the canonical
        ' simple shape. We load this at form-open time; if true,
        ' all the quick-config controls are disabled and the drift
        ' warning is shown instead.
        Private _isDrifted As Boolean

        Public Sub New(instanceId As String)
            FormIconHelper.ApplyTo(Me)
            _instanceId = instanceId
            InitializeControls()
            LoadExistingValues()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Edit Instance"
            ' Form is sized to fit the Restart Schedule section
            ' including the stagger + propagation rows added in
            ' Phase 4a continuation, plus the Instance Set field
            ' added in Phase 4b-pre1.
            Me.Size = New Size(580, 785)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20

            Dim nameLbl As New Label() With {
                .Text = "Instance Name:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(nameLbl)
            _nameTextBox = New TextBox() With {
                .Location = New Point(160, y), .Size = New Size(380, 24)}
            Me.Controls.Add(_nameTextBox)
            y += 35

            Dim exeLbl As New Label() With {
                .Text = "ExePath Override:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(exeLbl)
            _exeOverrideTextBox = New TextBox() With {
                .Location = New Point(160, y), .Size = New Size(380, 24)}
            Me.Controls.Add(_exeOverrideTextBox)
            y += 35

            _autoStartCheckBox = New CheckBox() With {
                .Text = "Start automatically with Manager",
                .Location = New Point(20, y), .AutoSize = True}
            Me.Controls.Add(_autoStartCheckBox)
            y += 35

            ' Instance Set tag — user-defined logical grouping
            ' label. Used by RuleScope.InstanceSet rules to
            ' resolve "all instances in this set" across
            ' installations and nodes (e.g. all Last Oasis
            ' instances on "realm-alpha" regardless of which
            ' install or node hosts each tile). Free-form
            ' string; autocomplete pulls from distinct existing
            ' values across the entire Instances table so users
            ' can stay consistent without having to define sets
            ' up front. Empty/blank means "not in any set".
            Dim setLbl As New Label() With {
                .Text = "Instance Set:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(setLbl)
            _instanceSetCombo = New ComboBox() With {
                .Location = New Point(160, y),
                .Size = New Size(380, 24),
                .DropDownStyle = ComboBoxStyle.DropDown,
                .AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                .AutoCompleteSource = AutoCompleteSource.ListItems}
            Me.Controls.Add(_instanceSetCombo)
            y += 35

            Dim configLabel As New Label() With {
                .Text = "Instance Configuration",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(20, y)}
            Me.Controls.Add(configLabel)
            y += 25

            _configPanel = New Panel() With {
                .Location = New Point(20, y),
                .Size = New Size(520, 220),
                .BorderStyle = BorderStyle.FixedSingle,
                .AutoScroll = True}
            Me.Controls.Add(_configPanel)
            y += 230

            ' ---- Restart Schedule section ----
            InitializeRestartSection(y)
            y += 265

            ' ---- Save / Cancel ----
            Dim saveBtn As New Button() With {
                .Text = "Save", .Size = New Size(100, 32),
                .Location = New Point(330, y)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)

            Dim cancelBtn As New Button() With {
                .Text = "Cancel", .Size = New Size(100, 32),
                .Location = New Point(440, y)}
            cancelBtn.DialogResult = DialogResult.Cancel
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn
        End Sub

        ''' <summary>
        ''' Build the Restart Schedule section at the given vertical
        ''' offset. The section has two mutually-exclusive panels:
        '''   _normalPanel  — standard cron/preset controls
        '''   _driftPanel   — warning + link to Automation Rules
        ''' ApplyDriftState toggles which one is visible.
        ''' </summary>
        Private Sub InitializeRestartSection(startY As Integer)
            Dim y = startY

            Dim header As New Label() With {
                .Text = "Restart Schedule",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(20, y)}
            Me.Controls.Add(header)
            y += 25

            ' Both panels occupy the same region below the header.
            ' Hidden panels don't render or tab-stop, so there's no
            ' layout collision even though the positions overlap.
            Dim panelBounds = New Rectangle(20, y, 520, 240)

            ' ---- Drift panel (hidden unless drift detected) ----
            _driftPanel = New Panel() With {
                .Location = panelBounds.Location,
                .Size = panelBounds.Size,
                .Visible = False}
            Me.Controls.Add(_driftPanel)

            _driftWarningLabel = New Label() With {
                .Text = "⚠ This rule has been customised in Automation Rules." & vbCrLf &
                        "   Open it there to view or edit the full configuration.",
                .ForeColor = Color.FromArgb(180, 100, 20),
                .AutoSize = True,
                .Location = New Point(0, 5)}
            _driftPanel.Controls.Add(_driftWarningLabel)

            _openInAutomationButton = New Button() With {
                .Text = "Open in Automation Rules...",
                .Size = New Size(220, 28),
                .Location = New Point(0, 50)}
            AddHandler _openInAutomationButton.Click, AddressOf OnOpenInAutomationRules
            _driftPanel.Controls.Add(_openInAutomationButton)

            ' ---- Normal panel (visible unless drift detected) ----
            _normalPanel = New Panel() With {
                .Location = panelBounds.Location,
                .Size = panelBounds.Size,
                .Visible = True}
            Me.Controls.Add(_normalPanel)

            Dim ny = 0  ' panel-local Y

            _restartEnabledCheckBox = New CheckBox() With {
                .Text = "Enable scheduled restart",
                .Location = New Point(0, ny),
                .AutoSize = True}
            AddHandler _restartEnabledCheckBox.CheckedChanged,
                AddressOf OnRestartEnabledChanged
            _normalPanel.Controls.Add(_restartEnabledCheckBox)
            ny += 30

            Dim cronLbl As New Label() With {
                .Text = "Cron:", .AutoSize = True,
                .Location = New Point(0, ny + 3)}
            _normalPanel.Controls.Add(cronLbl)

            _cronTextBox = New TextBox() With {
                .Location = New Point(50, ny),
                .Size = New Size(130, 24)}
            AddHandler _cronTextBox.TextChanged, AddressOf OnCronTextChanged
            _normalPanel.Controls.Add(_cronTextBox)

            _nextRunLabel = New Label() With {
                .Text = "",
                .ForeColor = Color.FromArgb(80, 80, 80),
                .AutoSize = False,
                .Size = New Size(350, 22),
                .AutoEllipsis = True,
                .Location = New Point(190, ny + 3)}
            _normalPanel.Controls.Add(_nextRunLabel)
            ny += 35

            ' Presets row — two paired numeric+button widgets.
            Dim presetLbl As New Label() With {
                .Text = "Presets:", .AutoSize = True,
                .Location = New Point(0, ny + 3)}
            _normalPanel.Controls.Add(presetLbl)

            Dim hourLbl As New Label() With {
                .Text = "Hour:", .AutoSize = True,
                .Location = New Point(65, ny + 3)}
            _normalPanel.Controls.Add(hourLbl)

            _dailyHourNumeric = New NumericUpDown() With {
                .Location = New Point(105, ny),
                .Size = New Size(50, 24),
                .Minimum = 0,
                .Maximum = 23,
                .Value = 4}
            _normalPanel.Controls.Add(_dailyHourNumeric)

            _setDailyButton = New Button() With {
                .Text = "Set Daily",
                .Size = New Size(80, 24),
                .Location = New Point(160, ny)}
            AddHandler _setDailyButton.Click, AddressOf OnSetDaily
            _normalPanel.Controls.Add(_setDailyButton)

            Dim everyLbl As New Label() With {
                .Text = "Every:", .AutoSize = True,
                .Location = New Point(260, ny + 3)}
            _normalPanel.Controls.Add(everyLbl)

            _intervalHoursNumeric = New NumericUpDown() With {
                .Location = New Point(305, ny),
                .Size = New Size(50, 24),
                .Minimum = 1,
                .Maximum = 24,
                .Value = 12}
            _normalPanel.Controls.Add(_intervalHoursNumeric)

            Dim hrsLbl As New Label() With {
                .Text = "hrs", .AutoSize = True,
                .Location = New Point(360, ny + 3)}
            _normalPanel.Controls.Add(hrsLbl)

            _setIntervalButton = New Button() With {
                .Text = "Set Interval",
                .Size = New Size(90, 24),
                .Location = New Point(390, ny)}
            AddHandler _setIntervalButton.Click, AddressOf OnSetInterval
            _normalPanel.Controls.Add(_setIntervalButton)
            ny += 35

            ' Stagger step — used by the "Stagger" propagation mode.
            ' Set 0 means literal copy when stagger mode is picked
            ' (functionally equivalent to picking the "Apply same"
            ' radio, kept for parity with users who prefer to leave
            ' the radio set and just zero the step).
            Dim staggerLbl As New Label() With {
                .Text = "Stagger step:", .AutoSize = True,
                .Location = New Point(0, ny + 3)}
            _normalPanel.Controls.Add(staggerLbl)

            _staggerStepNumeric = New NumericUpDown() With {
                .Location = New Point(85, ny),
                .Size = New Size(50, 24),
                .Minimum = 0,
                .Maximum = 60,
                .Value = 5}
            _normalPanel.Controls.Add(_staggerStepNumeric)

            Dim minLbl As New Label() With {
                .Text = "min",
                .AutoSize = True,
                .ForeColor = Color.FromArgb(120, 120, 120),
                .Font = New Font("Segoe UI", 8.5F),
                .Location = New Point(140, ny + 5)}
            _normalPanel.Controls.Add(minLbl)
            ny += 32

            ' Propagation mode — mutually exclusive radio group.
            ' Defaults to None (single-instance edit). The other two
            ' modes write to ENABLED siblings only; "Enable all"
            ' below extends what counts as enabled.
            Dim propLbl As New Label() With {
                .Text = "Propagation:",
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(0, ny)}
            _normalPanel.Controls.Add(propLbl)
            ny += 22

            _propagateNoneRadio = New RadioButton() With {
                .Text = "This instance only",
                .Checked = True,
                .AutoSize = True,
                .Location = New Point(15, ny)}
            _normalPanel.Controls.Add(_propagateNoneRadio)
            ny += 20

            _propagateStaggerRadio = New RadioButton() With {
                .Text = "Stagger across enabled siblings (renumber by SortOrder)",
                .AutoSize = True,
                .Location = New Point(15, ny)}
            _normalPanel.Controls.Add(_propagateStaggerRadio)
            ny += 20

            _propagateLiteralRadio = New RadioButton() With {
                .Text = "Apply same cron to enabled siblings (no stagger)",
                .AutoSize = True,
                .Location = New Point(15, ny)}
            _normalPanel.Controls.Add(_propagateLiteralRadio)
            ny += 25

            ' Enable on all: turns RestartEnabled = True on every
            ' sibling BEFORE the propagation runs. Combined with a
            ' propagation mode, this is the "set up a fresh
            ' installation in one go" workflow. One-way ON only —
            ' never disables siblings (avoids the "oops I disabled
            ' all my restarts" pandemonium scenario). To DISABLE
            ' siblings, edit each one individually.
            _enableOnAllCheckBox = New CheckBox() With {
                .Text = "Enable scheduled restart on all instances first",
                .Location = New Point(0, ny),
                .AutoSize = True}
            _normalPanel.Controls.Add(_enableOnAllCheckBox)
            ny += 25

            _restartHelpLabel = New Label() With {
                .Text = "ℹ Restarts are queued so only one instance per installation restarts" & vbCrLf &
                        "  at a time. The next instance begins when this one's tile has loaded." & vbCrLf &
                        "  For advanced setups (conditions, notifications), use Automation Rules.",
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Font = New Font("Segoe UI", 8.5F),
                .AutoSize = True,
                .Location = New Point(0, ny)}
            _normalPanel.Controls.Add(_restartHelpLabel)
        End Sub

        ' ============================================================
        '  Event handlers — restart section
        ' ============================================================

        ''' <summary>
        ''' Toggle the sub-controls' enabled state based on the
        ''' master checkbox. Doesn't touch the drift warning —
        ''' ApplyDriftState handles that separately.
        ''' </summary>
        Private Sub OnRestartEnabledChanged(sender As Object, e As EventArgs)
            UpdateRestartControlsEnabled()
            If _restartEnabledCheckBox.Checked Then
                RefreshNextRunPreview()
            Else
                _nextRunLabel.Text = "(scheduled restart disabled)"
            End If
        End Sub

        ''' <summary>
        ''' Applies enable state to every control EXCEPT the master
        ''' checkbox and the drift warning widgets. Called on check
        ''' change and on form load.
        ''' </summary>
        Private Sub UpdateRestartControlsEnabled()
            Dim enabled = _restartEnabledCheckBox.Checked AndAlso Not _isDrifted
            _cronTextBox.Enabled = enabled
            _dailyHourNumeric.Enabled = enabled
            _setDailyButton.Enabled = enabled
            _intervalHoursNumeric.Enabled = enabled
            _setIntervalButton.Enabled = enabled
            _staggerStepNumeric.Enabled = enabled
            _propagateNoneRadio.Enabled = enabled
            _propagateStaggerRadio.Enabled = enabled
            _propagateLiteralRadio.Enabled = enabled
            _enableOnAllCheckBox.Enabled = enabled
        End Sub

        ''' <summary>
        ''' Switch the section into drift mode (warning + link)
        ''' when the existing rule doesn't match the canonical
        ''' simple shape. Toggling at the panel level means we
        ''' don't have to manage visibility on every individual
        ''' control (including unstored static labels like "Cron:",
        ''' "Presets:", etc.).
        ''' </summary>
        Private Sub ApplyDriftState()
            If Not _isDrifted Then Return
            _normalPanel.Visible = False
            _driftPanel.Visible = True
        End Sub

        Private Sub OnCronTextChanged(sender As Object, e As EventArgs)
            RefreshNextRunPreview()
        End Sub

        ''' <summary>
        ''' Parse the cron field via NCrontab and show the next
        ''' occurrence. Invalid cron shows a red "invalid" label
        ''' rather than an exception dialog — users will be
        ''' mid-typing constantly.
        ''' </summary>
        Private Sub RefreshNextRunPreview()
            If _nextRunLabel Is Nothing Then Return
            If Not _restartEnabledCheckBox.Checked Then
                _nextRunLabel.Text = "(scheduled restart disabled)"
                _nextRunLabel.ForeColor = Color.FromArgb(80, 80, 80)
                Return
            End If

            Dim cron = _cronTextBox.Text.Trim()
            If String.IsNullOrEmpty(cron) Then
                _nextRunLabel.Text = "(enter a cron expression)"
                _nextRunLabel.ForeColor = Color.FromArgb(150, 100, 20)
                Return
            End If

            Try
                Dim schedule = NCrontab.CrontabSchedule.Parse(cron)
                Dim now = DateTime.Now
                Dim nextRun = schedule.GetNextOccurrence(now)
                Dim delta = nextRun - now
                _nextRunLabel.Text = $"Next: {nextRun:ddd h:mm tt}  (in {FormatDuration(delta)})"
                _nextRunLabel.ForeColor = Color.FromArgb(80, 80, 80)
            Catch
                _nextRunLabel.Text = "Invalid cron expression"
                _nextRunLabel.ForeColor = Color.FromArgb(180, 40, 40)
            End Try
        End Sub

        ''' <summary>
        ''' Turn a TimeSpan into a human-readable "3d 2h" / "18h 23m"
        ''' / "42m" string. Keeps the next-run label compact.
        ''' </summary>
        Private Shared Function FormatDuration(span As TimeSpan) As String
            If span.TotalSeconds < 0 Then Return "now"
            If span.TotalDays >= 1 Then
                Return $"{CInt(Math.Floor(span.TotalDays))}d {span.Hours}h"
            End If
            If span.TotalHours >= 1 Then
                Return $"{CInt(Math.Floor(span.TotalHours))}h {span.Minutes}m"
            End If
            Return $"{span.Minutes}m"
        End Function

        Private Sub OnSetDaily(sender As Object, e As EventArgs)
            Dim hour = CInt(_dailyHourNumeric.Value)
            _cronTextBox.Text = $"0 {hour} * * *"
        End Sub

        Private Sub OnSetInterval(sender As Object, e As EventArgs)
            Dim hours = CInt(_intervalHoursNumeric.Value)
            ' Guard: "*/1" is valid but unusual (every hour), "*/24"
            ' collapses to once a day at hour 0. Both are legal so we
            ' just write them.
            _cronTextBox.Text = $"0 */{hours} * * *"
        End Sub

        ''' <summary>
        ''' Apply a minute offset to a cron expression's minute field.
        ''' Handles fixed-minute crons ("0 4 * * *" → "15 4 * * *")
        ''' and bumps the hour when the minute wraps past 59 IF the
        ''' hour is also fixed. Returns Nothing if the minute field
        ''' isn't a plain integer (e.g. "*", "*/5", "0,30") because
        ''' offset semantics aren't well-defined there.
        '''
        ''' Negative offsets are supported — needed for the
        ''' propagation case where THIS instance is at SortOrder N
        ''' but a sibling at SortOrder M < N gets offset (M-N)*step
        ''' which is negative.
        ''' </summary>
        Friend Shared Function ApplyMinuteOffsetToCron(cron As String, offsetMinutes As Integer) As String
            If String.IsNullOrWhiteSpace(cron) Then Return Nothing
            Dim parts = cron.Trim().Split({" "c, ChrW(9)}, StringSplitOptions.RemoveEmptyEntries)
            If parts.Length < 5 Then Return Nothing

            Dim minuteField = parts(0)
            Dim hourField = parts(1)

            Dim baseMinute As Integer
            If Not Integer.TryParse(minuteField, baseMinute) Then
                Return Nothing
            End If

            Dim totalMinutes = baseMinute + offsetMinutes
            ' Floor-divide for the hour bump so negative offsets work
            ' correctly: e.g. base=30, offset=-40 → total=-10, want
            ' minute=50, hour-bump=-1. VB's \ operator truncates toward
            ' zero, so -10 \ 60 = 0 with remainder -10 — we'd lose
            ' the borrow. The double-mod trick produces the right
            ' minute, then we recompute the hour bump from
            ' (totalMinutes - newMinute) / 60 which is exact.
            Dim newMinute = ((totalMinutes Mod 60) + 60) Mod 60
            Dim hourBump = (totalMinutes - newMinute) \ 60

            Dim baseHour As Integer
            If hourBump <> 0 AndAlso Integer.TryParse(hourField, baseHour) Then
                Dim newHour = ((baseHour + hourBump) Mod 24 + 24) Mod 24
                parts(1) = newHour.ToString()
            End If

            parts(0) = newMinute.ToString()
            Return String.Join(" ", parts)
        End Function

        Private Sub OnOpenInAutomationRules(sender As Object, e As EventArgs)
            ' Close this modal first so we're not nesting a non-modal
            ' window inside a closing modal (causes parenting and
            ' focus oddities). Then route through MainForm.OnAutomationRules
            ' so the singleton-aware code path enforces "only one
            ' Automation Rules window at a time".
            Me.DialogResult = DialogResult.Cancel
            Me.Close()
            Dim mainForm = Application.OpenForms.OfType(Of MainForm)().FirstOrDefault()
            If mainForm IsNot Nothing Then
                mainForm.OnAutomationRules()
            End If
        End Sub

        ' ============================================================
        '  Load / Save
        ' ============================================================

        Private Sub LoadExistingValues()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(_instanceId)
                If instanceEntity Is Nothing Then Return

                _nameTextBox.Text = instanceEntity.DisplayName
                _exeOverrideTextBox.Text = If(instanceEntity.ExeOverride, "")
                _autoStartCheckBox.Checked = instanceEntity.AutoStart

                ' Populate the InstanceSet combo's autocomplete
                ' list with distinct existing tags from the whole
                ' Instances table. Order alphabetically so the
                ' dropdown is predictable. The CURRENT instance's
                ' tag goes in regardless (in case it's the only
                ' one with that tag, which is fine — a set of one
                ' is still a set).
                Dim distinctTags = db.Instances.
                    Where(Function(i) i.InstanceSetTag IsNot Nothing AndAlso
                                       i.InstanceSetTag <> "").
                    Select(Function(i) i.InstanceSetTag).
                    Distinct().
                    OrderBy(Function(t) t).
                    ToList()
                _instanceSetCombo.Items.Clear()
                For Each t In distinctTags
                    _instanceSetCombo.Items.Add(t)
                Next
                _instanceSetCombo.Text = If(instanceEntity.InstanceSetTag, "")

                ' ---- Load restart state ----
                ' Three cases to distinguish:
                '   1. No rule (RuleId null OR entity missing):
                '      "no rule" mode, load from instance fields.
                '   2. Rule exists and is simple: enable section,
                '      pull cron from the rule (authoritative) not
                '      from Instance.RestartCron (cached, can drift).
                '   3. Rule exists but is drifted: drift mode.
                Dim rule As AutomationRuleEntity = Nothing
                If Not String.IsNullOrEmpty(instanceEntity.RestartRuleId) Then
                    rule = db.AutomationRules.Find(instanceEntity.RestartRuleId)
                End If

                If rule IsNot Nothing AndAlso
                   Not RestartRuleMaterializer.IsSimpleRestartRule(rule) Then
                    _isDrifted = True
                    _restartEnabledCheckBox.Checked = rule.IsEnabled
                    _cronTextBox.Text = ""
                ElseIf rule IsNot Nothing Then
                    ' Simple rule — pull cron from rule, not instance
                    _restartEnabledCheckBox.Checked = instanceEntity.RestartEnabled AndAlso rule.IsEnabled
                    Dim cronFromRule = RestartRuleMaterializer.ExtractCronFromRule(rule)
                    _cronTextBox.Text = If(cronFromRule, If(instanceEntity.RestartCron, ""))
                Else
                    ' No rule (orphan-safe path too — even if
                    ' RestartRuleId was set but entity missing, we
                    ' treat it as fresh and let Materialize create
                    ' a new rule on save).
                    _restartEnabledCheckBox.Checked = instanceEntity.RestartEnabled
                    _cronTextBox.Text = If(instanceEntity.RestartCron, "")
                End If

                ApplyDriftState()
                UpdateRestartControlsEnabled()
                RefreshNextRunPreview()

                ' ---- Load config schema ----
                Dim existingValues As New Dictionary(Of String, String)
                If Not String.IsNullOrEmpty(instanceEntity.ConfigJson) Then
                    Try
                        existingValues = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(instanceEntity.ConfigJson)
                        If existingValues Is Nothing Then existingValues = New Dictionary(Of String, String)
                    Catch
                    End Try
                End If

                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return
                Dim gamePlugin = registry.GetPlugin(instanceEntity.GameId)
                If gamePlugin Is Nothing Then Return

                Dim schema = gamePlugin.GetInstanceConfigSchema()

                ' Filter out RCON fields if plugin has no RCON support
                If Not gamePlugin.GetRconProtocol().HasValue Then
                    schema = schema.Where(Function(f) _
                        Not String.Equals(f.Key, "RconPort", StringComparison.OrdinalIgnoreCase) AndAlso
                        Not String.Equals(f.Key, "RconPassword", StringComparison.OrdinalIgnoreCase)
                    ).ToList()
                End If

                schema = schema.Concat(CommonConfigFields.GetInstanceLifecycleFields()).ToList()

                ' Build a file-list provider so any ManagedFilePicker
                ' fields in the schema render with a populated dropdown.
                ' Captures only _instanceId; everything else is re-
                ' resolved on each call so a node addr / token edit
                ' takes effect on the next dropdown open without form
                ' rebuild. Returns Nothing on any failure path so the
                ' combo silently degrades to free-text-only.
                Dim fileListProvider As Func(Of String, Task(Of IReadOnlyList(Of String))) =
                    AddressOf BuildSavesProviderForCurrentInstance

                _schemaResult = SchemaFormBuilder.Build(schema, existingValues, fileListProvider)
                If _schemaResult.Panel IsNot Nothing Then
                    _schemaResult.Panel.Dock = DockStyle.Fill
                    _configPanel.Controls.Add(_schemaResult.Panel)
                End If
            End Using
        End Sub

        ''' <summary>
        ''' File-list provider for ManagedFilePicker fields on the
        ''' edit form. Looks up the instance's plugin, finds the
        ''' ManagedDirectory whose RelativePath matches dirRef, then
        ''' calls the node's ListFilesAsync endpoint and returns just
        ''' the basenames (the dropdown shows "foo.zip", not
        ''' "saves/foo.zip"). Returns an empty list on any failure —
        ''' the combo's free-text path stays usable regardless.
        '''
        ''' Why this lives on EditInstanceForm rather than as a
        ''' shared helper: the provider needs _instanceId in scope
        ''' and is the sole caller. If a second form needs the same
        ''' resolution — say AddInstanceForm gets a save picker too —
        ''' lift this to a shared static at that point. Right now
        ''' AddInstanceForm doesn't run because the install path may
        ''' not even exist yet, so there's nothing meaningful to list.
        ''' </summary>
        Private Async Function BuildSavesProviderForCurrentInstance(dirRef As String) As Task(Of IReadOnlyList(Of String))
            If String.IsNullOrEmpty(dirRef) Then Return New List(Of String)
            Try
                Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
                If factory Is Nothing Then Return New List(Of String)

                Dim installPath As String = Nothing
                Dim nodeId As String = Nothing
                Dim hostAddress As String = Nothing
                Dim port As Integer = 0
                Dim authToken As String = Nothing
                Dim gameId As String = Nothing
                Dim displayName As String = Nothing
                Dim installationId As String = Nothing

                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then Return New List(Of String)
                    Dim installEntity = db.Installations.Find(instanceEntity.InstallationId)
                    If installEntity Is Nothing Then Return New List(Of String)
                    Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                    If nodeEntity Is Nothing Then Return New List(Of String)

                    installPath = installEntity.InstallPath
                    nodeId = nodeEntity.NodeId
                    hostAddress = nodeEntity.HostAddress
                    port = nodeEntity.Port
                    authToken = nodeEntity.AuthToken
                    gameId = instanceEntity.GameId
                    displayName = instanceEntity.DisplayName
                    installationId = instanceEntity.InstallationId
                End Using

                ' Resolve the ManagedDirectory whose RelativePath
                ' matches dirRef so we use the plugin-declared
                ' permissions / extension allowlist on the node call.
                ' Falls back to a permissive call (no extension
                ' filter) if the lookup fails — the dropdown showing
                ' too many files is far better than showing none.
                Dim resolvedRel As String = dirRef
                Dim allowedExtensions As IReadOnlyList(Of String) = Nothing
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry IsNot Nothing Then
                    Dim plugin = registry.GetPlugin(gameId)
                    Dim provider = TryCast(plugin, IManagedDirectoriesProvider)
                    If provider IsNot Nothing Then
                        Dim minimalConfig As New InstanceConfig With {
                            .InstanceId = _instanceId,
                            .GameId = gameId,
                            .DisplayName = displayName,
                            .InstallationId = installationId
                        }
                        Dim dirs = provider.GetManagedDirectories(minimalConfig)
                        If dirs IsNot Nothing Then
                            For Each d In dirs
                                If d Is Nothing Then Continue For
                                If String.Equals(d.RelativePath, dirRef,
                                                  StringComparison.OrdinalIgnoreCase) Then
                                    resolvedRel = If(d.RelativePath, dirRef).
                                        Replace("{InstanceId}", _instanceId)
                                    allowedExtensions = d.AllowedExtensions
                                    Exit For
                                End If
                            Next
                        End If
                    End If
                End If

                Dim client = factory.GetClient(nodeId, hostAddress, port, authToken)
                Dim allowedRoots As IReadOnlyList(Of String) = New String() {resolvedRel}

                Dim entries = Await client.ListFilesAsync(
                    _instanceId,
                    installPath,
                    resolvedRel,
                    allowedRoots,
                    allowedExtensions,
                    System.Threading.CancellationToken.None)

                If entries Is Nothing Then Return New List(Of String)

                ' Strip the directory prefix — the dropdown shows
                ' "foo.zip", not "saves/foo.zip". Sort newest-first by
                ' ModifiedUtc so a freshly-uploaded backup lands at
                ' the top of the dropdown without the user having to
                ' scroll. Matches the ordering the ManagedFilesPanel
                ' already uses for the same listing.
                Return entries.
                    OrderByDescending(Function(f) f.ModifiedUtc).
                    Select(Function(f) ShortName(f.RelativePath)).
                    Where(Function(n) Not String.IsNullOrEmpty(n)).
                    ToList()
            Catch
                Return New List(Of String)
            End Try
        End Function

        Private Shared Function ShortName(relativePath As String) As String
            If String.IsNullOrEmpty(relativePath) Then Return ""
            Dim slashIdx = relativePath.LastIndexOfAny(New Char() {"/"c, "\"c})
            If slashIdx < 0 Then Return relativePath
            Return relativePath.Substring(slashIdx + 1)
        End Function

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Instance name is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' Validate cron if restart is enabled AND not drifted.
            ' (If drifted, the cron field wasn't editable; skip check.)
            If _restartEnabledCheckBox.Checked AndAlso Not _isDrifted Then
                Dim cronText = _cronTextBox.Text.Trim()
                If String.IsNullOrEmpty(cronText) Then
                    MessageBox.Show("Cron expression is required when scheduled restart is enabled.",
                                    "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
                Try
                    NCrontab.CrontabSchedule.Parse(cronText)
                Catch ex As Exception
                    MessageBox.Show($"Invalid cron expression: {ex.Message}",
                                    "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End Try
            End If

            Dim configValues As New Dictionary(Of String, String)
            If _schemaResult IsNot Nothing AndAlso _schemaResult.ValueExtractor IsNot Nothing Then
                configValues = _schemaResult.ValueExtractor.Invoke()
            End If

            ' Port conflict check before any DB writes. Same
            ' warn-and-confirm policy as AddInstanceForm — see
            ' the comment there for the reasoning. selfInstanceId
            ' is passed so the instance's own current port values
            ' (still in DB at this point) don't appear as conflicts
            ' against its proposed values: the form is editing
            ' THIS instance, not adding a new one.
            '
            ' We don't validate the propagation siblings' ports
            ' against each other here — propagation only writes
            ' RestartCron, never port fields. Sibling configs are
            ' untouched by this form's save path.
            Try
                Using checkScope = ManagerProgram.Services.CreateScope()
                    Dim checkDb = checkScope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instForCheck = checkDb.Instances.Find(_instanceId)
                    If instForCheck IsNot Nothing Then
                        Dim installForCheck = checkDb.Installations.Find(instForCheck.InstallationId)
                        Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                        If installForCheck IsNot Nothing AndAlso registry IsNot Nothing Then
                            Dim plugin = registry.GetPlugin(instForCheck.GameId)
                            If plugin IsNot Nothing Then
                                Dim conflicts = PortAllocator.FindPortConflicts(
                                    plugin, installForCheck.NodeId,
                                    _instanceId, configValues, checkDb)
                                If conflicts.Count > 0 Then
                                    Dim msg = "Port conflicts detected:" & vbCrLf & vbCrLf &
                                        PortAllocator.FormatConflictsForDisplay(conflicts) & vbCrLf &
                                        "Conflicting ports will fail to bind when both servers run at the same time." & vbCrLf &
                                        "Save anyway?"
                                    Dim res = MessageBox.Show(msg, "Port Conflicts",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                                    If res <> DialogResult.Yes Then Return
                                End If
                            End If
                        End If
                    End If
                End Using
            Catch
                ' Validation failure shouldn't block save — worst
                ' case the user gets a fail-to-bind on Start, which
                ' is the same behaviour as before this check existed.
            End Try

            ' anyRuleChanged tracks whether ANY rule (this instance's
            ' or a sibling's via propagation) changed, so we know
            ' whether ReloadRules is warranted at the end. Spurious
            ' reloads are cheap but spam the log; skip when nothing
            ' actually changed.
            Dim anyRuleChanged As Boolean = False

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(_instanceId)
                If instanceEntity Is Nothing Then Return

                instanceEntity.DisplayName = _nameTextBox.Text.Trim()
                instanceEntity.ExeOverride = _exeOverrideTextBox.Text.Trim()
                instanceEntity.AutoStart = _autoStartCheckBox.Checked
                ' Trim and normalise empty-or-whitespace to Nothing
                ' so the DB doesn't end up with rows holding empty
                ' strings that wouldn't match anything anyway. The
                ' InstanceSet scope query uses string equality, so
                ' "" and Nothing should behave identically; storing
                ' Nothing keeps the data shape clean.
                Dim setTag = _instanceSetCombo.Text.Trim()
                instanceEntity.InstanceSetTag = If(String.IsNullOrEmpty(setTag), Nothing, setTag)
                instanceEntity.ConfigJson = JsonSerializer.Serialize(configValues)
                instanceEntity.UpdatedUtc = DateTime.UtcNow

                ' Update this instance's restart fields only when NOT
                ' drifted — drifted rules keep their power-user edits
                ' intact. Propagation logic also short-circuits in
                ' drift mode (handled by the radio buttons being
                ' disabled, but we re-check here defensively).
                Dim thisCron As String = Nothing
                If Not _isDrifted Then
                    instanceEntity.RestartEnabled = _restartEnabledCheckBox.Checked
                    thisCron = If(_restartEnabledCheckBox.Checked,
                                   _cronTextBox.Text.Trim(),
                                   Nothing)
                    instanceEntity.RestartCron = thisCron
                    Dim result = RestartRuleMaterializer.Materialize(db, instanceEntity)
                    If result.Action <> RestartRuleMaterializer.MaterializationAction.NoChange Then
                        anyRuleChanged = True
                    End If
                End If

                ' ---- Propagation ----
                ' Order:
                '   1. Enable-on-all (one-way ON to siblings) — must
                '      run BEFORE the propagation step so newly-enabled
                '      siblings count as enabled for the propagation
                '      sibling-set.
                '   2. Propagation mode (None / Stagger / Literal).
                If Not _isDrifted AndAlso _restartEnabledCheckBox.Checked Then
                    Dim siblings = db.Instances.
                        Where(Function(i) i.InstallationId = instanceEntity.InstallationId AndAlso
                                           i.InstanceId <> _instanceId).
                        ToList()

                    ' Step 1: Enable on all (if requested).
                    If _enableOnAllCheckBox.Checked Then
                        For Each sib In siblings
                            If IsSiblingDrifted(db, sib) Then Continue For
                            If Not sib.RestartEnabled Then
                                sib.RestartEnabled = True
                                sib.UpdatedUtc = DateTime.UtcNow
                                ' Don't materialise yet — the propagation
                                ' step below may also touch this sibling's
                                ' cron, and we want one materialise per
                                ' sibling, not two.
                            End If
                        Next
                    End If

                    ' Step 2: Propagation. Only siblings that are now
                    ' RestartEnabled=true (whether they started that way
                    ' or got flipped by step 1) participate. The two
                    ' modes differ in what cron each gets.
                    If _propagateStaggerRadio.Checked OrElse _propagateLiteralRadio.Checked Then
                        Dim stepMin = CInt(_staggerStepNumeric.Value)

                        ' Build the renumbered active list (this
                        ' instance + enabled siblings, sorted by
                        ' SortOrder, position 1..N). Drift-skipped
                        ' siblings are excluded — they don't get a
                        ' position assigned and won't be touched.
                        Dim activeSet As New List(Of InstanceEntity)
                        activeSet.Add(instanceEntity)
                        For Each sib In siblings
                            If IsSiblingDrifted(db, sib) Then Continue For
                            If sib.RestartEnabled Then activeSet.Add(sib)
                        Next
                        activeSet = activeSet.
                            OrderBy(Function(i) i.SortOrder).
                            ThenBy(Function(i) i.CreatedUtc).
                            ToList()

                        ' Find this instance's renumbered position.
                        Dim thisPosition As Integer = activeSet.
                            FindIndex(Function(i) i.InstanceId = _instanceId)
                        If thisPosition < 0 Then thisPosition = 0

                        For Each sib In activeSet
                            If sib.InstanceId = _instanceId Then Continue For

                            Dim sibPosition = activeSet.IndexOf(sib)
                            Dim newCron As String

                            If _propagateStaggerRadio.Checked Then
                                ' Stagger: anchor on this instance.
                                ' Sibling at active-position M relative
                                ' to me at active-position N gets
                                ' minute = thisMinute + (M - N) * step.
                                Dim offset = (sibPosition - thisPosition) * stepMin
                                newCron = ApplyMinuteOffsetToCron(thisCron, offset)
                                If newCron Is Nothing Then
                                    ' Anchor cron isn't suitable for
                                    ' offset math (non-numeric minute).
                                    ' Fall back to literal copy for
                                    ' this sibling rather than skipping
                                    ' — user expressed propagation
                                    ' intent and a literal copy is
                                    ' "better than nothing".
                                    newCron = thisCron
                                End If
                            Else
                                ' Literal mode: every sibling gets
                                ' the same cron value as this instance.
                                newCron = thisCron
                            End If

                            If sib.RestartCron <> newCron Then
                                sib.RestartCron = newCron
                                sib.UpdatedUtc = DateTime.UtcNow
                            End If
                        Next
                    End If

                    ' Materialise rules for every sibling we touched.
                    ' Whether the change was flip-to-enabled or cron
                    ' update or both, Materialize handles the
                    ' create/update/delete correctly. No-op for
                    ' siblings whose state didn't actually change.
                    For Each sib In siblings
                        If IsSiblingDrifted(db, sib) Then Continue For
                        Dim sibResult = RestartRuleMaterializer.Materialize(db, sib)
                        If sibResult.Action <> RestartRuleMaterializer.MaterializationAction.NoChange Then
                            anyRuleChanged = True
                        End If
                    Next
                End If

                db.SaveChanges()
            End Using

            ' Reload the engine if anything actually changed. Skips
            ' the reload + log spam on no-op saves.
            If anyRuleChanged Then
                Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
                engine?.ReloadRules()
            End If

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        ''' <summary>
        ''' Drift check on a sibling: we don't touch a sibling whose
        ''' rule has been customised in Automation Rules. Same
        ''' "preserve power-user edits" principle that applies to
        ''' this instance, just applied to siblings.
        '''
        ''' Note: needs the db scope active so the AutomationRules
        ''' lookup is on the same change-tracker context as the
        ''' caller's mutations. Caller passes the db in directly.
        ''' </summary>
        Private Shared Function IsSiblingDrifted(db As GsmDbContext,
                                                  sib As InstanceEntity) As Boolean
            If sib Is Nothing Then Return False
            If String.IsNullOrEmpty(sib.RestartRuleId) Then Return False
            Dim rule = db.AutomationRules.Find(sib.RestartRuleId)
            If rule Is Nothing Then Return False
            Return Not RestartRuleMaterializer.IsSimpleRestartRule(rule)
        End Function

    End Class

    ' ============================================================
    '  EditInstallationForm — edit an existing installation's config
    '  (e.g. CustomerKey/ProviderKey for Last Oasis).
    ' ============================================================

    Public Class EditInstallationForm
        Inherits Form

        Private ReadOnly _installationId As String
        Private _nameTextBox As TextBox
        Private _pathLabel As Label
        Private _credLabel As Label
        Private _steamCredCombo As ComboBox
        Private _runRedistCheckBox As CheckBox
        Private _configPanel As Panel
        Private _schemaResult As SchemaFormResult

        Public Sub New(installationId As String)
            FormIconHelper.ApplyTo(Me)
            _installationId = installationId
            InitializeControls()
            LoadExistingValues()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Edit Installation"
            Me.Size = New Size(580, 640)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim y = 20

            Dim nameLbl As New Label() With {
                .Text = "Display Name:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(nameLbl)
            _nameTextBox = New TextBox() With {
                .Location = New Point(160, y), .Size = New Size(380, 24)}
            Me.Controls.Add(_nameTextBox)
            y += 35

            Dim pathCaption As New Label() With {
                .Text = "Install Path:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(pathCaption)
            _pathLabel = New Label() With {
                .AutoSize = True, .ForeColor = Color.Gray,
                .Location = New Point(160, y + 3),
                .MaximumSize = New Size(380, 0)}
            Me.Controls.Add(_pathLabel)
            y += 35

            ' Steam credential dropdown — lets the user pick which
            ' Steam account (if any) is associated with this installation.
            ' Stored credentials come from the Steam Credentials form in
            ' Tools → Settings.
            '
            ' For non-SteamCmd installs (DirectDownload, Manual) the
            ' credential isn't used by the install runner, so the row
            ' is hidden in LoadExistingValues based on the entity's
            ' InstallMethod — leaving it visible would suggest the
            ' choice mattered when it doesn't. The label is tracked
            ' as a field so we can hide it alongside the combo.
            _credLabel = New Label() With {
                .Text = "Steam Account:", .AutoSize = True,
                .Location = New Point(20, y + 3)}
            Me.Controls.Add(_credLabel)
            _steamCredCombo = New ComboBox() With {
                .Location = New Point(160, y),
                .Size = New Size(380, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList}
            Me.Controls.Add(_steamCredCombo)
            y += 35

            ' Run _CommonRedist toggle — off by default since most
            ' machines already have the redistributables, and without
            ' an elevated node each redist triggers a UAC prompt.
            _runRedistCheckBox = New CheckBox() With {
                .Text = "Run _CommonRedist installers after install (requires elevated node)",
                .AutoSize = True,
                .Location = New Point(160, y + 3)}
            Me.Controls.Add(_runRedistCheckBox)
            y += 30

            Dim configLabel As New Label() With {
                .Text = "Installation Configuration",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .AutoSize = True, .Location = New Point(20, y)}
            Me.Controls.Add(configLabel)
            y += 25

            _configPanel = New Panel() With {
                .Location = New Point(20, y),
                .Size = New Size(520, 330),
                .BorderStyle = BorderStyle.FixedSingle,
                .AutoScroll = True}
            Me.Controls.Add(_configPanel)
            y += 340

            Dim saveBtn As New Button() With {
                .Text = "Save", .Size = New Size(100, 32),
                .Location = New Point(330, y)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)

            Dim cancelBtn As New Button() With {
                .Text = "Cancel", .Size = New Size(100, 32),
                .Location = New Point(440, y)}
            cancelBtn.DialogResult = DialogResult.Cancel
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn
        End Sub

        Private Sub LoadExistingValues()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(_installationId)
                If installEntity Is Nothing Then Return

                _nameTextBox.Text = installEntity.DisplayName
                _pathLabel.Text = installEntity.InstallPath
                _runRedistCheckBox.Checked = installEntity.RunCommonRedist

                ' Hide the Steam-credential row entirely for non-
                ' SteamCmd installs. Mirrors NewInstallationForm's
                ' OnMethodChanged behaviour. Install method can't
                ' be changed after creation so this is a one-shot
                ' visibility set, not a live toggle.
                Dim isSteamInstall = String.Equals(installEntity.InstallMethod,
                                                    InstallMethod.SteamCmd.ToString(),
                                                    StringComparison.OrdinalIgnoreCase)
                If _credLabel IsNot Nothing Then _credLabel.Visible = isSteamInstall
                If _steamCredCombo IsNot Nothing Then _steamCredCombo.Visible = isSteamInstall

                ' Populate the Steam credential dropdown with all stored
                ' credentials plus an "(anonymous)" option. Tag each item
                ' with the CredentialId so we can read it back on save.
                _steamCredCombo.Items.Clear()
                Dim anonItem As New SteamCredItem("", "(anonymous — default)")
                _steamCredCombo.Items.Add(anonItem)
                Dim selectedIndex = 0
                Dim idx = 1
                For Each cred In db.SteamCredentials.ToList()
                    Dim label = If(Not String.IsNullOrEmpty(cred.DisplayName),
                                    $"{cred.DisplayName} ({cred.Username})",
                                    cred.Username)
                    Dim item As New SteamCredItem(cred.CredentialId, label)
                    _steamCredCombo.Items.Add(item)
                    If Not String.IsNullOrEmpty(installEntity.SteamCredentialId) AndAlso
                       cred.CredentialId = installEntity.SteamCredentialId Then
                        selectedIndex = idx
                    End If
                    idx += 1
                Next
                _steamCredCombo.SelectedIndex = selectedIndex

                Dim existingValues As New Dictionary(Of String, String)
                If Not String.IsNullOrEmpty(installEntity.ConfigJson) Then
                    Try
                        existingValues = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(installEntity.ConfigJson)
                        If existingValues Is Nothing Then existingValues = New Dictionary(Of String, String)
                    Catch
                    End Try
                End If

                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return
                Dim gamePlugin = registry.GetPlugin(installEntity.GameId)
                If gamePlugin Is Nothing Then Return

                Dim schema = gamePlugin.GetInstallConfigSchema()
                _schemaResult = SchemaFormBuilder.Build(schema, existingValues)
                If _schemaResult.Panel IsNot Nothing Then
                    _schemaResult.Panel.Dock = DockStyle.Fill
                    _configPanel.Controls.Add(_schemaResult.Panel)
                End If
            End Using
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                MessageBox.Show("Display name is required.", "Validation",
                              MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim configValues As New Dictionary(Of String, String)
            If _schemaResult IsNot Nothing AndAlso _schemaResult.ValueExtractor IsNot Nothing Then
                configValues = _schemaResult.ValueExtractor.Invoke()
            End If

            ' Read the selected Steam credential — empty CredId means anonymous.
            Dim selectedCredId As String = ""
            Dim selectedItem = TryCast(_steamCredCombo.SelectedItem, SteamCredItem)
            If selectedItem IsNot Nothing Then selectedCredId = selectedItem.CredId

            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim installEntity = db.Installations.Find(_installationId)
                If installEntity Is Nothing Then Return

                installEntity.DisplayName = _nameTextBox.Text.Trim()
                installEntity.ConfigJson = JsonSerializer.Serialize(configValues)
                installEntity.SteamCredentialId = If(String.IsNullOrEmpty(selectedCredId),
                                                      Nothing, selectedCredId)
                installEntity.RunCommonRedist = _runRedistCheckBox.Checked
                installEntity.UpdatedUtc = DateTime.UtcNow
                db.SaveChanges()
            End Using

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        ' Holder so we can show labels in the combo while keeping
        ' the underlying CredentialId accessible for save.
        Private Class SteamCredItem
            Public ReadOnly CredId As String
            Public ReadOnly Label As String
            Public Sub New(credId As String, label As String)
                Me.CredId = credId
                Me.Label = label
            End Sub
            Public Overrides Function ToString() As String
                Return Label
            End Function
        End Class

    End Class

End Namespace