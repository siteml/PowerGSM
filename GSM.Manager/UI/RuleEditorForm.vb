Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Automation
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Plugin

' ============================================================
'  RuleEditorForm — author / edit a full automation rule
'
'  Phase 4b-3 polish: tabbed layout. Form was getting tall
'  (~935px) with all four sections stacked vertically; tabs
'  drop it to ~400px and group fields functionally instead
'  of by typography.
'
'  Layout:
'    Header strip (Name + Enabled, always visible)
'    TabControl with 4 tabs:
'      Rule       — Scope, GameFilter, Target, Overlap
'      Trigger    — type combo + sub-editor
'      Conditions — Mode + Add/Edit/Remove/↑/↓ + listbox
'      Action     — type combo + sub-editor (incl. sequence)
'    Save / Cancel
'
'  Validation glyphs: when Save fails per-tab validation, the
'  broken tab gets a "⚠ " prefix (Segoe UI renders the warning
'  glyph cleanly) and the form switches to that tab so the
'  user sees the inline error message in context. Glyph stays
'  until the next Save attempt clears it. Asymmetric "show
'  only when broken" pattern — adding ✓ to good tabs every
'  time would feel like the form is grading you.
'
'  Round-trips through AutomationRuleSerializer so any rule
'  the editor produces is fully readable by the engine and
'  by future versions of this form (no schema drift between
'  the form and the JSON contract).
'
'  Sub-editor pattern (unchanged): each trigger / action type
'  has a small builder method that returns a TriggerSubEditor /
'  ActionSubEditor record bundling its Panel, a "build current
'  values into an ITrigger/IAction" function, and a "load an
'  existing ITrigger/IAction into the controls" Sub. Action
'  builders live in ActionEditorFactory so StepEditorForm can
'  share them; only SequenceAction's sub-editor lives here
'  because it needs mutable form state.
' ============================================================

Namespace GSM.Manager.UI

    Public Class RuleEditorForm
        Inherits Form

        Private ReadOnly _editRuleId As String

        ' ---- Cached lookup data, loaded once at form construction ----
        Private _instances As List(Of InstanceEntity)
        Private _installations As List(Of InstallationEntity)
        Private _nodes As List(Of NodeEntity)
        Private _notificationDestinations As List(Of NotificationDestinationEntity)
        Private _distinctSetTags As List(Of String)
        Private _distinctGameIds As List(Of String)

        ' ---- Header strip controls (always visible above tabs) ----
        Private _nameTextBox As TextBox
        Private _enabledCheckBox As CheckBox

        ' ---- Tab control + per-tab pages ----
        Private _tabs As TabControl
        Private _ruleTab As TabPage
        Private _triggerTab As TabPage
        Private _conditionsTab As TabPage
        Private _actionTab As TabPage

        ' ---- Rule-tab controls (Scope/Target/GameFilter/Overlap) ----
        Private _scopeComboBox As ComboBox
        Private _gameFilterComboBox As ComboBox
        Private _targetLabel As Label
        Private _targetComboBox As ComboBox
        Private _overlapComboBox As ComboBox

        ' ---- Trigger tab ----
        Private _triggerTypeCombo As ComboBox
        Private _triggerSubPanel As Panel
        Private _currentTriggerEditor As TriggerSubEditor

        ' ---- Conditions tab ----
        Private _conditions As List(Of ICondition)
        Private _conditionsListBox As ListBox
        Private _conditionModeCombo As ComboBox

        ' ---- Action tab ----
        Private _actionTypeCombo As ComboBox
        Private _actionSubPanel As Panel
        Private _currentActionEditor As ActionSubEditor
        Private _actionFactory As ActionEditorFactory

        ' Phase 4b-3 — sequence editor state. Lives at form level
        ' so it survives action-type switches (user can flip
        ' through types and come back without losing their step
        ' work).
        Private _sequenceSteps As List(Of IAction)
        Private _sequenceContinueOnFailure As Boolean = False

        ' ---- Tab caption tracking for validation glyphs ----
        '
        ' Plain captions stored at construction so we can restore
        ' them after a previous validation pass added the "⚠ "
        ' prefix. Keyed by TabPage reference.
        Private _plainTabCaptions As Dictionary(Of TabPage, String)

        Public Sub New(Optional editRuleId As String = Nothing)
            FormIconHelper.ApplyTo(Me)
            _editRuleId = editRuleId
            _conditions = New List(Of ICondition)
            _sequenceSteps = New List(Of IAction)
            _plainTabCaptions = New Dictionary(Of TabPage, String)
            LoadLookupData()
            _actionFactory = New ActionEditorFactory(
                _instances, _installations, _notificationDestinations)
            InitializeControls()
            If Not String.IsNullOrEmpty(_editRuleId) Then
                LoadExistingRule()
            Else
                ' Defaults for new-rule mode. Calling these AFTER
                ' control construction because they touch combos.
                OnScopeChanged()
                OnTriggerTypeChanged()
                OnActionTypeChanged()
            End If
        End Sub

        ' ============================================================
        '  Lookup data — pre-loaded once so type-switch handlers don't
        '  have to round-trip to the DB every time the user picks a
        '  different scope/action.
        ' ============================================================

        Private Sub LoadLookupData()
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    _instances = db.Instances.AsNoTracking().ToList()
                    _installations = db.Installations.AsNoTracking().ToList()
                    _nodes = db.Nodes.AsNoTracking().ToList()
                    _notificationDestinations = db.NotificationDestinations.
                        AsNoTracking().Where(Function(d) d.Enabled).ToList()
                    _distinctSetTags = db.Instances.
                        Where(Function(i) i.InstanceSetTag IsNot Nothing AndAlso
                                           i.InstanceSetTag <> "").
                        Select(Function(i) i.InstanceSetTag).
                        Distinct().OrderBy(Function(t) t).ToList()
                    _distinctGameIds = db.Installations.
                        Select(Function(i) i.GameId).
                        Distinct().OrderBy(Function(g) g).ToList()
                End Using
            Catch
                ' Defensive empty lists — form still opens, just
                ' shows empty dropdowns rather than crashing.
            End Try
            If _instances Is Nothing Then _instances = New List(Of InstanceEntity)
            If _installations Is Nothing Then _installations = New List(Of InstallationEntity)
            If _nodes Is Nothing Then _nodes = New List(Of NodeEntity)
            If _notificationDestinations Is Nothing Then _notificationDestinations = New List(Of NotificationDestinationEntity)
            If _distinctSetTags Is Nothing Then _distinctSetTags = New List(Of String)
            If _distinctGameIds Is Nothing Then _distinctGameIds = New List(Of String)
        End Sub

        ' ============================================================
        '  Layout — tabbed (Phase 4b-3 polish)
        ' ============================================================

        Private Sub InitializeControls()
            Me.Text = If(String.IsNullOrEmpty(_editRuleId), "New Rule", "Edit Rule")
            ' Form sized to fit:
            '   header strip (~50)
            '   tab control (~290 — 25 tab strip + 260 page + 5 margin)
            '   buttons row (~50)
            '   margins (~30)
            ' Total ~420 — comfortable on any modern display.
            Me.Size = New Size(760, 480)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            BuildHeaderStrip()
            BuildTabControl()
            BuildButtons()
        End Sub

        ' ---- Header strip (Name + Enabled, always visible) ----

        Private Sub BuildHeaderStrip()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim nameLbl As New Label() With {
                .Text = "Name:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(20, 18)}
            Me.Controls.Add(nameLbl)
            _nameTextBox = New TextBox() With {
                .Location = New Point(75, 15), .Size = New Size(500, 24),
                .Font = contentFont}
            Me.Controls.Add(_nameTextBox)
            _enabledCheckBox = New CheckBox() With {
                .Text = "Enabled", .Checked = True, .AutoSize = True,
                .Font = contentFont,
                .Location = New Point(595, 17)}
            Me.Controls.Add(_enabledCheckBox)
        End Sub

        ' ---- TabControl with the 4 functional sections ----

        Private Sub BuildTabControl()
            _tabs = New TabControl() With {
                .Location = New Point(15, 50),
                .Size = New Size(720, 330),
                .Font = New Font("Segoe UI", 9)}
            Me.Controls.Add(_tabs)

            _ruleTab = New TabPage("Rule")
            _triggerTab = New TabPage("Trigger")
            _conditionsTab = New TabPage("Conditions")
            _actionTab = New TabPage("Action")

            _tabs.TabPages.AddRange(New TabPage() {
                _ruleTab, _triggerTab, _conditionsTab, _actionTab})

            ' Stash plain captions so the validation glyph code can
            ' restore them after a previous Save attempt added the
            ' warning prefix.
            _plainTabCaptions(_ruleTab) = "Rule"
            _plainTabCaptions(_triggerTab) = "Trigger"
            _plainTabCaptions(_conditionsTab) = "Conditions"
            _plainTabCaptions(_actionTab) = "Action"

            BuildRuleTab()
            BuildTriggerTab()
            BuildConditionsTab()
            BuildActionTab()
        End Sub

        ' ---- Rule tab: Scope / Target / GameFilter / Overlap ----

        Private Sub BuildRuleTab()
            Dim contentFont As New Font("Segoe UI", 9)
            Dim lblY = 20
            Dim ctrlY = 17
            Dim rowH = 35

            ' Row 1: Scope + GameFilter
            Dim scopeLbl As New Label() With {
                .Text = "Scope:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, lblY)}
            _ruleTab.Controls.Add(scopeLbl)
            _scopeComboBox = New ComboBox() With {
                .Location = New Point(80, ctrlY), .Size = New Size(180, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _scopeComboBox.Items.AddRange(New Object() {
                New IdItem(RuleScope.Instance.ToString(), "Instance"),
                New IdItem(RuleScope.Installation.ToString(), "Installation"),
                New IdItem(RuleScope.Node.ToString(), "Node"),
                New IdItem(RuleScope.InstanceSet.ToString(), "Instance Set"),
                New IdItem(RuleScope.AllInstances.ToString(), "All Instances")})
            _scopeComboBox.SelectedIndex = 0
            AddHandler _scopeComboBox.SelectedIndexChanged, Sub(s, e) OnScopeChanged()
            _ruleTab.Controls.Add(_scopeComboBox)

            Dim gameLbl As New Label() With {
                .Text = "Game filter:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(290, lblY)}
            _ruleTab.Controls.Add(gameLbl)
            _gameFilterComboBox = New ComboBox() With {
                .Location = New Point(370, ctrlY), .Size = New Size(180, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _gameFilterComboBox.Items.Add(New IdItem("", "(any game)"))
            For Each gid In _distinctGameIds
                _gameFilterComboBox.Items.Add(New IdItem(gid, gid))
            Next
            _gameFilterComboBox.SelectedIndex = 0
            _ruleTab.Controls.Add(_gameFilterComboBox)
            lblY += rowH : ctrlY += rowH

            ' Row 2: Target (full width — name can be long)
            _targetLabel = New Label() With {
                .Text = "Target instance:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, lblY)}
            _ruleTab.Controls.Add(_targetLabel)
            _targetComboBox = New ComboBox() With {
                .Location = New Point(120, ctrlY), .Size = New Size(560, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _ruleTab.Controls.Add(_targetComboBox)
            lblY += rowH : ctrlY += rowH

            ' Row 3: Overlap
            Dim overlapLbl As New Label() With {
                .Text = "On overlap:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, lblY)}
            _ruleTab.Controls.Add(overlapLbl)
            _overlapComboBox = New ComboBox() With {
                .Location = New Point(95, ctrlY), .Size = New Size(220, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _overlapComboBox.Items.AddRange(New Object() {
                New IdItem(OverlapPolicy.SkipIfRunning.ToString(), "Skip if already running"),
                New IdItem(OverlapPolicy.QueueNext.ToString(), "Queue next firing"),
                New IdItem(OverlapPolicy.CancelAndRestart.ToString(), "Cancel running, restart")})
            _overlapComboBox.SelectedIndex = 0
            _ruleTab.Controls.Add(_overlapComboBox)
        End Sub

        ' ---- Trigger tab ----

        Private Sub BuildTriggerTab()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim typeLbl As New Label() With {
                .Text = "Type:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, 20)}
            _triggerTab.Controls.Add(typeLbl)
            _triggerTypeCombo = New ComboBox() With {
                .Location = New Point(60, 17), .Size = New Size(220, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _triggerTypeCombo.Items.AddRange(New Object() {
                New IdItem("schedule", "Scheduled (cron)"),
                New IdItem("state_change", "Instance state change"),
                New IdItem("version_mismatch", "Update available"),
                New IdItem("manual", "Manual fire only")})
            _triggerTypeCombo.SelectedIndex = 0
            AddHandler _triggerTypeCombo.SelectedIndexChanged,
                Sub(s, e) OnTriggerTypeChanged()
            _triggerTab.Controls.Add(_triggerTypeCombo)

            ' Sub-editor host. Generous height — the trigger sub-
            ' editors are small (cron preview is the largest at
            ' ~80px); the rest of the tab page is unused but the
            ' sub-panel's bordered background visually frames it.
            _triggerSubPanel = New Panel() With {
                .Location = New Point(15, 55),
                .Size = New Size(680, 240),
                .BackColor = SystemColors.Window,
                .BorderStyle = BorderStyle.FixedSingle}
            _triggerTab.Controls.Add(_triggerSubPanel)
        End Sub

        ' ---- Conditions tab ----

        Private Sub BuildConditionsTab()
            Dim contentFont As New Font("Segoe UI", 9)

            ' Row 1: Mode + buttons
            Dim modeLbl As New Label() With {
                .Text = "Mode:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, 20)}
            _conditionsTab.Controls.Add(modeLbl)
            _conditionModeCombo = New ComboBox() With {
                .Location = New Point(60, 17), .Size = New Size(160, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _conditionModeCombo.Items.AddRange(New Object() {
                New IdItem(ConditionMode.All.ToString(), "All must pass"),
                New IdItem(ConditionMode.Any.ToString(), "Any must pass")})
            _conditionModeCombo.SelectedIndex = 0
            _conditionsTab.Controls.Add(_conditionModeCombo)

            Dim addBtn As New Button() With {
                .Text = "Add", .Size = New Size(60, 24),
                .Location = New Point(240, 17), .Font = contentFont}
            AddHandler addBtn.Click, AddressOf OnAddCondition
            _conditionsTab.Controls.Add(addBtn)

            Dim editBtn As New Button() With {
                .Text = "Edit", .Size = New Size(60, 24),
                .Location = New Point(305, 17), .Font = contentFont}
            AddHandler editBtn.Click, AddressOf OnEditCondition
            _conditionsTab.Controls.Add(editBtn)

            Dim removeBtn As New Button() With {
                .Text = "Remove", .Size = New Size(70, 24),
                .Location = New Point(370, 17), .Font = contentFont}
            AddHandler removeBtn.Click, AddressOf OnRemoveCondition
            _conditionsTab.Controls.Add(removeBtn)

            Dim upBtn As New Button() With {
                .Text = "↑", .Size = New Size(36, 24),
                .Location = New Point(450, 17), .Font = contentFont}
            AddHandler upBtn.Click, AddressOf OnMoveConditionUp
            _conditionsTab.Controls.Add(upBtn)

            Dim downBtn As New Button() With {
                .Text = "↓", .Size = New Size(36, 24),
                .Location = New Point(491, 17), .Font = contentFont}
            AddHandler downBtn.Click, AddressOf OnMoveConditionDown
            _conditionsTab.Controls.Add(downBtn)

            ' Listbox fills the rest of the tab. IntegralHeight=False
            ' to avoid silent height rounding to multiples of item
            ' height (which produces blank stripes at the bottom).
            _conditionsListBox = New ListBox() With {
                .Location = New Point(15, 55),
                .Size = New Size(680, 240),
                .Font = contentFont,
                .IntegralHeight = False}
            AddHandler _conditionsListBox.DoubleClick, AddressOf OnEditCondition
            _conditionsTab.Controls.Add(_conditionsListBox)
        End Sub

        ' ---- Action tab ----

        Private Sub BuildActionTab()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim typeLbl As New Label() With {
                .Text = "Type:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, 20)}
            _actionTab.Controls.Add(typeLbl)
            _actionTypeCombo = New ComboBox() With {
                .Location = New Point(60, 17), .Size = New Size(280, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _actionTypeCombo.Items.AddRange(New Object() {
                New IdItem("coordinated_restart", "Coordinated Restart (recommended)"),
                New IdItem("start_instance", "Start Instance"),
                New IdItem("stop_instance", "Stop Instance"),
                New IdItem("restart_instance", "Restart Instance (basic)"),
                New IdItem("start_all_instances", "Start All Instances (in installation)"),
                New IdItem("stop_all_instances", "Stop All Instances (in installation)"),
                New IdItem("update_installation", "Update Installation"),
                New IdItem("send_rcon", "Send RCON Command"),
                New IdItem("notify", "Send Notification"),
                New IdItem("wait", "Wait"),
                New IdItem("wait_for_ready", "Wait For Ready Signal"),
                New IdItem("sequence", "Sequence (multi-step)")})
            _actionTypeCombo.SelectedIndex = 0
            AddHandler _actionTypeCombo.SelectedIndexChanged,
                Sub(s, e) OnActionTypeChanged()
            _actionTab.Controls.Add(_actionTypeCombo)

            ' Action sub-panel — fills the rest of the tab. Largest
            ' sub-editor (sequence with listbox + buttons + hint)
            ' uses ~200px; other sub-editors are smaller and the
            ' bordered background frames the empty space cleanly.
            _actionSubPanel = New Panel() With {
                .Location = New Point(15, 55),
                .Size = New Size(680, 240),
                .BackColor = SystemColors.Window,
                .BorderStyle = BorderStyle.FixedSingle}
            _actionTab.Controls.Add(_actionSubPanel)
        End Sub

        Private Sub BuildButtons()
            ' Buttons sit ~10px below the tab control's bottom edge
            ' (tab at y=50, height 330, so y=380 + 10 margin = 390).
            Dim saveBtn As New Button() With {
                .Text = "Save", .Size = New Size(100, 32),
                .Location = New Point(525, 395)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)
            Me.AcceptButton = saveBtn

            Dim cancelBtn As New Button() With {
                .Text = "Cancel", .Size = New Size(100, 32),
                .Location = New Point(635, 395),
                .DialogResult = DialogResult.Cancel}
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn
        End Sub

        ' ============================================================
        '  Tab caption helpers — validation glyph management
        ' ============================================================

        ''' <summary>
        ''' Reset every tab caption to its plain (no-glyph) text.
        ''' Called at the start of OnSave's validation pass so a
        ''' previous failure's glyphs don't persist past the next
        ''' attempt.
        ''' </summary>
        Private Sub ClearTabValidationGlyphs()
            For Each kvp In _plainTabCaptions
                kvp.Key.Text = kvp.Value
            Next
        End Sub

        ''' <summary>
        ''' Mark a tab as having a validation problem by prefixing
        ''' its caption with "⚠ " (Segoe UI warning glyph). Idempotent
        ''' — calling twice doesn't double the prefix.
        ''' </summary>
        Private Sub MarkTabBroken(tab As TabPage)
            If tab Is Nothing Then Return
            Dim plain As String = Nothing
            If Not _plainTabCaptions.TryGetValue(tab, plain) Then plain = tab.Text
            tab.Text = "⚠ " & plain
        End Sub

        ' ============================================================
        '  Conditions list mutation
        ' ============================================================

        Private Sub OnAddCondition(sender As Object, e As EventArgs)
            Using dlg As New ConditionEditorForm(
                _instances, _installations, _nodes,
                _distinctSetTags, _distinctGameIds)
                If dlg.ShowDialog(Me) = DialogResult.OK AndAlso
                   dlg.ResultCondition IsNot Nothing Then
                    _conditions.Add(dlg.ResultCondition)
                    RefreshConditionsList()
                    _conditionsListBox.SelectedIndex = _conditions.Count - 1
                End If
            End Using
        End Sub

        Private Sub OnEditCondition(sender As Object, e As EventArgs)
            Dim idx = _conditionsListBox.SelectedIndex
            If idx < 0 OrElse idx >= _conditions.Count Then Return
            Dim existing = _conditions(idx)
            Using dlg As New ConditionEditorForm(
                _instances, _installations, _nodes,
                _distinctSetTags, _distinctGameIds, existing)
                If dlg.ShowDialog(Me) = DialogResult.OK AndAlso
                   dlg.ResultCondition IsNot Nothing Then
                    _conditions(idx) = dlg.ResultCondition
                    RefreshConditionsList()
                    _conditionsListBox.SelectedIndex = idx
                End If
            End Using
        End Sub

        Private Sub OnRemoveCondition(sender As Object, e As EventArgs)
            Dim idx = _conditionsListBox.SelectedIndex
            If idx < 0 OrElse idx >= _conditions.Count Then Return
            _conditions.RemoveAt(idx)
            RefreshConditionsList()
            If _conditions.Count > 0 Then
                _conditionsListBox.SelectedIndex = Math.Min(idx, _conditions.Count - 1)
            End If
        End Sub

        Private Sub OnMoveConditionUp(sender As Object, e As EventArgs)
            Dim idx = _conditionsListBox.SelectedIndex
            If idx <= 0 OrElse idx >= _conditions.Count Then Return
            Dim tmp = _conditions(idx)
            _conditions(idx) = _conditions(idx - 1)
            _conditions(idx - 1) = tmp
            RefreshConditionsList()
            _conditionsListBox.SelectedIndex = idx - 1
        End Sub

        Private Sub OnMoveConditionDown(sender As Object, e As EventArgs)
            Dim idx = _conditionsListBox.SelectedIndex
            If idx < 0 OrElse idx >= _conditions.Count - 1 Then Return
            Dim tmp = _conditions(idx)
            _conditions(idx) = _conditions(idx + 1)
            _conditions(idx + 1) = tmp
            RefreshConditionsList()
            _conditionsListBox.SelectedIndex = idx + 1
        End Sub

        Private Sub RefreshConditionsList()
            _conditionsListBox.BeginUpdate()
            Try
                _conditionsListBox.Items.Clear()
                For Each c In _conditions
                    _conditionsListBox.Items.Add(SummarizeCondition(c))
                Next
            Finally
                _conditionsListBox.EndUpdate()
            End Try
        End Sub

        Private Function SummarizeCondition(c As ICondition) As String
            If c Is Nothing Then Return "(null condition)"

            If TypeOf c Is InstanceStateCondition Then
                Dim ic = DirectCast(c, InstanceStateCondition)
                Dim instName = LookupInstanceName(ic.InstanceId)
                Return $"Instance State: {instName} is {ic.RequiredState}"
            End If

            If TypeOf c Is WaitForPlayerCountCondition Then
                Dim wc = DirectCast(c, WaitForPlayerCountCondition)
                Dim instName = LookupInstanceName(wc.InstanceId)
                Dim pollSec = wc.PollIntervalMs \ 1000
                Dim suffix As String
                If wc.TimeoutMs > 0 Then
                    suffix = $" ({pollSec}s poll, {wc.TimeoutMs \ 1000}s timeout)"
                Else
                    suffix = $" ({pollSec}s poll)"
                End If
                Return $"Wait for Player Count ≤ {wc.MaxPlayers} on {instName}{suffix}"
            End If

            If TypeOf c Is AllInstancesEmptyCondition Then
                Dim ac = DirectCast(c, AllInstancesEmptyCondition)
                Dim scopePrefix As String
                Dim targetName As String
                Select Case ac.Scope
                    Case RuleScope.Installation
                        scopePrefix = "installation"
                        targetName = LookupInstallationName(ac.TargetId)
                    Case RuleScope.Node
                        scopePrefix = "node"
                        targetName = LookupNodeName(ac.TargetId)
                    Case RuleScope.InstanceSet
                        scopePrefix = "set"
                        targetName = $"""{If(ac.TargetId, "")}"""
                    Case RuleScope.AllInstances
                        scopePrefix = "all instances"
                        targetName = ""
                    Case Else
                        scopePrefix = ac.Scope.ToString().ToLower()
                        targetName = If(ac.TargetId, "")
                End Select
                Dim core = If(String.IsNullOrEmpty(targetName),
                              $"All Empty: {scopePrefix}",
                              $"All Empty: {scopePrefix} {targetName}")
                If Not String.IsNullOrEmpty(ac.GameFilter) Then
                    core &= $" ({ac.GameFilter} only)"
                End If
                If ac.MaxPlayers > 0 Then
                    core &= $" ≤ {ac.MaxPlayers}"
                End If
                Return core
            End If

            Return If(c.DisplayLabel, c.GetType().Name)
        End Function

        Private Function LookupInstanceName(id As String) As String
            If String.IsNullOrEmpty(id) Then Return "(unset)"
            Dim inst = _instances.FirstOrDefault(Function(i) i.InstanceId = id)
            If inst IsNot Nothing Then Return inst.DisplayName
            Return id
        End Function

        Private Function LookupInstallationName(id As String) As String
            If String.IsNullOrEmpty(id) Then Return "(unset)"
            Dim ins = _installations.FirstOrDefault(Function(i) i.InstallationId = id)
            If ins IsNot Nothing Then Return ins.DisplayName
            Return id
        End Function

        Private Function LookupNodeName(id As String) As String
            If String.IsNullOrEmpty(id) Then Return "(unset)"
            Dim n = _nodes.FirstOrDefault(Function(x) x.NodeId = id)
            If n IsNot Nothing Then Return n.DisplayName
            Return id
        End Function

        Private Function LookupDestinationName(id As String) As String
            If String.IsNullOrEmpty(id) Then Return "(unset)"
            Dim d = _notificationDestinations.FirstOrDefault(Function(x) x.DestinationId = id)
            If d IsNot Nothing Then Return d.DisplayName
            Return id
        End Function

        ' ============================================================
        '  Scope / target combo coordination
        ' ============================================================

        Private Sub OnScopeChanged()
            Dim scope = GetSelectedScope()

            _targetComboBox.Items.Clear()
            _targetComboBox.Text = ""

            Select Case scope
                Case RuleScope.Instance
                    _targetLabel.Text = "Target instance:"
                    _targetLabel.Visible = True
                    _targetComboBox.Visible = True
                    _targetComboBox.DropDownStyle = ComboBoxStyle.DropDownList
                    For Each inst In _instances.OrderBy(Function(i) i.DisplayName)
                        _targetComboBox.Items.Add(
                            New IdItem(inst.InstanceId,
                                       $"{inst.DisplayName} ({inst.GameId})"))
                    Next

                Case RuleScope.Installation
                    _targetLabel.Text = "Target installation:"
                    _targetLabel.Visible = True
                    _targetComboBox.Visible = True
                    _targetComboBox.DropDownStyle = ComboBoxStyle.DropDownList
                    For Each ins In _installations.OrderBy(Function(i) i.DisplayName)
                        _targetComboBox.Items.Add(
                            New IdItem(ins.InstallationId,
                                       $"{ins.DisplayName} ({ins.GameId})"))
                    Next

                Case RuleScope.Node
                    _targetLabel.Text = "Target node:"
                    _targetLabel.Visible = True
                    _targetComboBox.Visible = True
                    _targetComboBox.DropDownStyle = ComboBoxStyle.DropDownList
                    For Each n In _nodes.OrderBy(Function(x) x.DisplayName)
                        _targetComboBox.Items.Add(
                            New IdItem(n.NodeId,
                                       $"{n.DisplayName} ({n.HostAddress}:{n.Port})"))
                    Next

                Case RuleScope.InstanceSet
                    _targetLabel.Text = "Target set:"
                    _targetLabel.Visible = True
                    _targetComboBox.Visible = True
                    _targetComboBox.DropDownStyle = ComboBoxStyle.DropDown
                    _targetComboBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend
                    _targetComboBox.AutoCompleteSource = AutoCompleteSource.ListItems
                    ' Loop variable named `setTag` not `tag` — Form.Tag
                    ' inheritance gotcha (BC30039).
                    For Each setTag In _distinctSetTags
                        _targetComboBox.Items.Add(setTag)
                    Next

                Case RuleScope.AllInstances
                    _targetLabel.Visible = False
                    _targetComboBox.Visible = False
            End Select
        End Sub

        ' ============================================================
        '  Trigger sub-editor swap + builders
        ' ============================================================

        Private Sub OnTriggerTypeChanged()
            _triggerSubPanel.Controls.Clear()
            Dim selectedItem = TryCast(_triggerTypeCombo.SelectedItem, IdItem)
            Dim id As String = If(selectedItem IsNot Nothing, selectedItem.Id, "schedule")
            Select Case id
                Case "schedule" : _currentTriggerEditor = BuildScheduleTriggerEditor()
                Case "state_change" : _currentTriggerEditor = BuildStateChangeTriggerEditor()
                Case "version_mismatch" : _currentTriggerEditor = BuildVersionMismatchTriggerEditor()
                Case "manual" : _currentTriggerEditor = BuildManualTriggerEditor()
                Case Else : _currentTriggerEditor = BuildScheduleTriggerEditor()
            End Select
            If _currentTriggerEditor IsNot Nothing AndAlso _currentTriggerEditor.Panel IsNot Nothing Then
                _currentTriggerEditor.Panel.Dock = DockStyle.Fill
                _triggerSubPanel.Controls.Add(_currentTriggerEditor.Panel)
            End If
        End Sub

        Private Function BuildScheduleTriggerEditor() As TriggerSubEditor
            Dim panel As New Panel()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim cronLbl As New Label() With {
                .Text = "Cron:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 12)}
            panel.Controls.Add(cronLbl)
            Dim cronTxt As New TextBox() With {
                .Location = New Point(50, 9), .Size = New Size(180, 24),
                .Font = contentFont, .Text = "0 4 * * *"}
            panel.Controls.Add(cronTxt)
            Dim previewLbl As New Label() With {
                .Location = New Point(240, 12), .AutoSize = False,
                .Size = New Size(420, 22), .AutoEllipsis = True,
                .ForeColor = Color.FromArgb(80, 80, 80), .Font = contentFont}
            panel.Controls.Add(previewLbl)

            Dim hourLbl As New Label() With {
                .Text = "Hour:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 47)}
            panel.Controls.Add(hourLbl)
            Dim hourNum As New NumericUpDown() With {
                .Location = New Point(50, 44), .Size = New Size(50, 24),
                .Minimum = 0, .Maximum = 23, .Value = 4, .Font = contentFont}
            panel.Controls.Add(hourNum)
            Dim setDailyBtn As New Button() With {
                .Text = "Set Daily", .Size = New Size(80, 24),
                .Location = New Point(105, 43)}
            AddHandler setDailyBtn.Click,
                Sub(s, e) cronTxt.Text = $"0 {CInt(hourNum.Value)} * * *"
            panel.Controls.Add(setDailyBtn)

            Dim everyLbl As New Label() With {
                .Text = "Every:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(210, 47)}
            panel.Controls.Add(everyLbl)
            Dim everyNum As New NumericUpDown() With {
                .Location = New Point(255, 44), .Size = New Size(50, 24),
                .Minimum = 1, .Maximum = 24, .Value = 12, .Font = contentFont}
            panel.Controls.Add(everyNum)
            Dim hrsLbl As New Label() With {
                .Text = "hrs", .AutoSize = True, .Font = contentFont,
                .Location = New Point(310, 47)}
            panel.Controls.Add(hrsLbl)
            Dim setIntBtn As New Button() With {
                .Text = "Set Interval", .Size = New Size(90, 24),
                .Location = New Point(340, 43)}
            AddHandler setIntBtn.Click,
                Sub(s, e) cronTxt.Text = $"0 */{CInt(everyNum.Value)} * * *"
            panel.Controls.Add(setIntBtn)

            AddHandler cronTxt.TextChanged,
                Sub(s, e) UpdateCronPreview(cronTxt.Text, previewLbl)
            UpdateCronPreview(cronTxt.Text, previewLbl)

            Return New TriggerSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New ScheduleTrigger With {
                    .CronExpression = cronTxt.Text.Trim()}, ITrigger),
                .LoadFn = Sub(t)
                              Dim st = TryCast(t, ScheduleTrigger)
                              If st IsNot Nothing Then
                                  cronTxt.Text = If(st.CronExpression, "")
                              End If
                          End Sub}
        End Function

        Private Sub UpdateCronPreview(cron As String, lbl As Label)
            Dim trimmed = If(cron, "").Trim()
            If String.IsNullOrEmpty(trimmed) Then
                lbl.Text = "(enter a cron expression)"
                lbl.ForeColor = Color.FromArgb(150, 100, 20)
                Return
            End If
            Try
                Dim sched = NCrontab.CrontabSchedule.Parse(trimmed)
                Dim now = DateTime.Now
                Dim nextRun = sched.GetNextOccurrence(now)
                lbl.Text = $"Next: {nextRun:ddd MMM d, h:mm tt}"
                lbl.ForeColor = Color.FromArgb(80, 80, 80)
            Catch
                lbl.Text = "Invalid cron expression"
                lbl.ForeColor = Color.FromArgb(180, 40, 40)
            End Try
        End Sub

        Private Function BuildStateChangeTriggerEditor() As TriggerSubEditor
            Dim panel As New Panel()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim fromLbl As New Label() With {
                .Text = "From state:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 12)}
            panel.Controls.Add(fromLbl)
            Dim fromCombo As New ComboBox() With {
                .Location = New Point(95, 9), .Size = New Size(170, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            fromCombo.Items.Add(New IdItem("", "(any)"))
            For Each st In [Enum].GetValues(GetType(InstanceState))
                fromCombo.Items.Add(New IdItem(st.ToString(), st.ToString()))
            Next
            fromCombo.SelectedIndex = 0
            panel.Controls.Add(fromCombo)

            Dim toLbl As New Label() With {
                .Text = "To state:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 47)}
            panel.Controls.Add(toLbl)
            Dim toCombo As New ComboBox() With {
                .Location = New Point(95, 44), .Size = New Size(170, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            toCombo.Items.Add(New IdItem("", "(any)"))
            For Each st In [Enum].GetValues(GetType(InstanceState))
                toCombo.Items.Add(New IdItem(st.ToString(), st.ToString()))
            Next
            toCombo.SelectedIndex = 0
            panel.Controls.Add(toCombo)

            Dim help As New Label() With {
                .Text = "Both fields are optional — leave as ""(any)"" to match any state.",
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 75)}
            panel.Controls.Add(help)

            Return New TriggerSubEditor With {
                .Panel = panel,
                .BuildFn = Function() As ITrigger
                               Dim t As New StateChangeTrigger()
                               Dim fromItem = TryCast(fromCombo.SelectedItem, IdItem)
                               Dim toItem = TryCast(toCombo.SelectedItem, IdItem)
                               If fromItem IsNot Nothing AndAlso
                                  Not String.IsNullOrEmpty(fromItem.Id) Then
                                   Dim fs As InstanceState
                                   If [Enum].TryParse(fromItem.Id, fs) Then
                                       t.FromState = fs
                                   End If
                               End If
                               If toItem IsNot Nothing AndAlso
                                  Not String.IsNullOrEmpty(toItem.Id) Then
                                   Dim ts As InstanceState
                                   If [Enum].TryParse(toItem.Id, ts) Then
                                       t.ToState = ts
                                   End If
                               End If
                               Return t
                           End Function,
                .LoadFn = Sub(trigger)
                              Dim sct = TryCast(trigger, StateChangeTrigger)
                              If sct Is Nothing Then Return
                              SelectComboById(fromCombo,
                                  If(sct.FromState.HasValue, sct.FromState.Value.ToString(), ""))
                              SelectComboById(toCombo,
                                  If(sct.ToState.HasValue, sct.ToState.Value.ToString(), ""))
                          End Sub}
        End Function

        Private Function BuildVersionMismatchTriggerEditor() As TriggerSubEditor
            Dim panel As New Panel()
            Dim help As New Label() With {
                .Text = "Fires when an installation's installed version differs from the latest available." & vbCrLf &
                        "Wired via AutomationEngine.RaiseVersionMismatchAsync — currently called by plugins or external version-check tools, not yet by an automatic polling service.",
                .AutoSize = True, .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(80, 80, 80),
                .Location = New Point(10, 12)}
            panel.Controls.Add(help)
            Return New TriggerSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New VersionMismatchTrigger(), ITrigger),
                .LoadFn = Sub(t)
                          End Sub}
        End Function

        Private Function BuildManualTriggerEditor() As TriggerSubEditor
            Dim panel As New Panel()
            Dim help As New Label() With {
                .Text = "Fires only when ""Fire Now"" is clicked in the Automation Rules window," & vbCrLf &
                        "or when invoked from a notification plugin's remote-command handler.",
                .AutoSize = True, .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(80, 80, 80),
                .Location = New Point(10, 12)}
            panel.Controls.Add(help)
            Return New TriggerSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New ManualTrigger(), ITrigger),
                .LoadFn = Sub(t)
                          End Sub}
        End Function

        ' ============================================================
        '  Action sub-editor swap
        ' ============================================================

        Private Sub OnActionTypeChanged()
            _actionSubPanel.Controls.Clear()
            Dim selectedItem = TryCast(_actionTypeCombo.SelectedItem, IdItem)
            Dim id As String = If(selectedItem IsNot Nothing, selectedItem.Id, "coordinated_restart")
            ' Sequence is special: its sub-editor is the step-list
            ' UI which lives in this form (not the factory) because
            ' it needs access to the form's _sequenceSteps mutable
            ' state and the StepEditorForm modal launcher.
            If id = "sequence" Then
                _currentActionEditor = BuildSequenceEditor()
            Else
                _currentActionEditor = _actionFactory.BuildEditor(id)
            End If
            If _currentActionEditor IsNot Nothing AndAlso _currentActionEditor.Panel IsNot Nothing Then
                _currentActionEditor.Panel.Dock = DockStyle.Fill
                _actionSubPanel.Controls.Add(_currentActionEditor.Panel)
            End If
        End Sub

        ' ============================================================
        '  Sequence sub-editor (Phase 4b-3)
        ' ============================================================

        Private Function BuildSequenceEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim contentFont As New Font("Segoe UI", 9)

            ' Top row: buttons + ContinueOnFailure
            Dim addBtn As New Button() With {
                .Text = "Add", .Size = New Size(60, 24),
                .Location = New Point(10, 8), .Font = contentFont}
            AddHandler addBtn.Click, AddressOf OnAddStep
            panel.Controls.Add(addBtn)

            Dim editBtn As New Button() With {
                .Text = "Edit", .Size = New Size(60, 24),
                .Location = New Point(75, 8), .Font = contentFont}
            AddHandler editBtn.Click, AddressOf OnEditStep
            panel.Controls.Add(editBtn)

            Dim removeBtn As New Button() With {
                .Text = "Remove", .Size = New Size(70, 24),
                .Location = New Point(140, 8), .Font = contentFont}
            AddHandler removeBtn.Click, AddressOf OnRemoveStep
            panel.Controls.Add(removeBtn)

            Dim upBtn As New Button() With {
                .Text = "↑", .Size = New Size(36, 24),
                .Location = New Point(220, 8), .Font = contentFont}
            AddHandler upBtn.Click, AddressOf OnMoveStepUp
            panel.Controls.Add(upBtn)

            Dim downBtn As New Button() With {
                .Text = "↓", .Size = New Size(36, 24),
                .Location = New Point(261, 8), .Font = contentFont}
            AddHandler downBtn.Click, AddressOf OnMoveStepDown
            panel.Controls.Add(downBtn)

            Dim continueChk As New CheckBox() With {
                .Text = "Continue on step failure",
                .Location = New Point(310, 10), .AutoSize = True,
                .Font = contentFont,
                .Checked = _sequenceContinueOnFailure}
            AddHandler continueChk.CheckedChanged,
                Sub(s, e) _sequenceContinueOnFailure = continueChk.Checked
            panel.Controls.Add(continueChk)

            ' Step list. Tab-bound sub-panel is now ~240px tall, so
            ' the listbox can be ~170px (visible ~10 rows).
            Dim listBox As New ListBox() With {
                .Location = New Point(10, 38),
                .Size = New Size(660, 170),
                .Font = contentFont,
                .IntegralHeight = False}
            AddHandler listBox.DoubleClick, AddressOf OnEditStep
            panel.Controls.Add(listBox)

            ' Persistence hint: only shown when there are steps.
            If _sequenceSteps.Count > 0 Then
                Dim persistHint As New Label() With {
                    .AutoSize = True,
                    .Font = New Font("Segoe UI", 8.25F, FontStyle.Italic),
                    .ForeColor = Color.FromArgb(100, 100, 100),
                    .Location = New Point(10, 214),
                    .MaximumSize = New Size(650, 0),
                    .Text = "Steps are kept while you have this rule open — switching action types and back won't lose them. Cancel discards; Save persists."}
                panel.Controls.Add(persistHint)
            End If

            ' Tag the panel so handlers can find the active listbox
            ' without form-level fields for transient sub-editor
            ' state.
            panel.Tag = listBox

            RefreshSequenceList(listBox)

            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() As IAction
                               Return New SequenceAction With {
                                   .Steps = New List(Of IAction)(_sequenceSteps),
                                   .ContinueOnFailure = _sequenceContinueOnFailure}
                           End Function,
                .LoadFn = Sub(a)
                              Dim sa = TryCast(a, SequenceAction)
                              If sa Is Nothing Then Return
                              _sequenceSteps = New List(Of IAction)(
                                  If(sa.Steps, New List(Of IAction)))
                              _sequenceContinueOnFailure = sa.ContinueOnFailure
                              continueChk.Checked = sa.ContinueOnFailure
                              RefreshSequenceList(listBox)
                          End Sub}
        End Function

        ' ---- Sequence list mutation handlers ----

        Private Function GetSequenceListBox() As ListBox
            If _currentActionEditor Is Nothing Then Return Nothing
            If _currentActionEditor.Panel Is Nothing Then Return Nothing
            Return TryCast(_currentActionEditor.Panel.Tag, ListBox)
        End Function

        Private Sub OnAddStep(sender As Object, e As EventArgs)
            Dim listBox = GetSequenceListBox()
            If listBox Is Nothing Then Return
            Using dlg As New StepEditorForm(_actionFactory)
                If dlg.ShowDialog(Me) = DialogResult.OK AndAlso
                   dlg.ResultStep IsNot Nothing Then
                    _sequenceSteps.Add(dlg.ResultStep)
                    RefreshSequenceList(listBox)
                    listBox.SelectedIndex = _sequenceSteps.Count - 1
                End If
            End Using
        End Sub

        Private Sub OnEditStep(sender As Object, e As EventArgs)
            Dim listBox = GetSequenceListBox()
            If listBox Is Nothing Then Return
            Dim idx = listBox.SelectedIndex
            If idx < 0 OrElse idx >= _sequenceSteps.Count Then Return
            Dim existing = _sequenceSteps(idx)
            Using dlg As New StepEditorForm(_actionFactory, existing)
                If dlg.ShowDialog(Me) = DialogResult.OK AndAlso
                   dlg.ResultStep IsNot Nothing Then
                    _sequenceSteps(idx) = dlg.ResultStep
                    RefreshSequenceList(listBox)
                    listBox.SelectedIndex = idx
                End If
            End Using
        End Sub

        Private Sub OnRemoveStep(sender As Object, e As EventArgs)
            Dim listBox = GetSequenceListBox()
            If listBox Is Nothing Then Return
            Dim idx = listBox.SelectedIndex
            If idx < 0 OrElse idx >= _sequenceSteps.Count Then Return
            _sequenceSteps.RemoveAt(idx)
            RefreshSequenceList(listBox)
            If _sequenceSteps.Count > 0 Then
                listBox.SelectedIndex = Math.Min(idx, _sequenceSteps.Count - 1)
            End If
        End Sub

        Private Sub OnMoveStepUp(sender As Object, e As EventArgs)
            Dim listBox = GetSequenceListBox()
            If listBox Is Nothing Then Return
            Dim idx = listBox.SelectedIndex
            If idx <= 0 OrElse idx >= _sequenceSteps.Count Then Return
            Dim tmp = _sequenceSteps(idx)
            _sequenceSteps(idx) = _sequenceSteps(idx - 1)
            _sequenceSteps(idx - 1) = tmp
            RefreshSequenceList(listBox)
            listBox.SelectedIndex = idx - 1
        End Sub

        Private Sub OnMoveStepDown(sender As Object, e As EventArgs)
            Dim listBox = GetSequenceListBox()
            If listBox Is Nothing Then Return
            Dim idx = listBox.SelectedIndex
            If idx < 0 OrElse idx >= _sequenceSteps.Count - 1 Then Return
            Dim tmp = _sequenceSteps(idx)
            _sequenceSteps(idx) = _sequenceSteps(idx + 1)
            _sequenceSteps(idx + 1) = tmp
            RefreshSequenceList(listBox)
            listBox.SelectedIndex = idx + 1
        End Sub

        Private Sub RefreshSequenceList(listBox As ListBox)
            If listBox Is Nothing Then Return
            listBox.BeginUpdate()
            Try
                listBox.Items.Clear()
                For i = 0 To _sequenceSteps.Count - 1
                    listBox.Items.Add($"{i + 1}. {SummarizeStep(_sequenceSteps(i))}")
                Next
            Finally
                listBox.EndUpdate()
            End Try
        End Sub

        Private Function SummarizeStep(a As IAction) As String
            If a Is Nothing Then Return "(null step)"

            If TypeOf a Is CoordinatedRestartAction Then
                Dim ca = DirectCast(a, CoordinatedRestartAction)
                Return $"Coordinated Restart: {LookupInstanceName(ca.InstanceId)}"
            ElseIf TypeOf a Is StartInstanceAction Then
                Dim sa = DirectCast(a, StartInstanceAction)
                Return $"Start: {LookupInstanceName(sa.InstanceId)}"
            ElseIf TypeOf a Is StopInstanceAction Then
                Dim sa = DirectCast(a, StopInstanceAction)
                Return $"Stop: {LookupInstanceName(sa.InstanceId)}"
            ElseIf TypeOf a Is RestartInstanceAction Then
                Dim ra = DirectCast(a, RestartInstanceAction)
                Return $"Restart: {LookupInstanceName(ra.InstanceId)}"
            ElseIf TypeOf a Is StartAllInstancesAction Then
                Dim sa = DirectCast(a, StartAllInstancesAction)
                Return $"Start all: {LookupInstallationName(sa.InstallationId)}"
            ElseIf TypeOf a Is StopAllInstancesAction Then
                Dim sa = DirectCast(a, StopAllInstancesAction)
                Return $"Stop all: {LookupInstallationName(sa.InstallationId)}"
            ElseIf TypeOf a Is UpdateInstallationAction Then
                Dim ua = DirectCast(a, UpdateInstallationAction)
                Return $"Update: {LookupInstallationName(ua.InstallationId)}"
            ElseIf TypeOf a Is SendRconCommandAction Then
                Dim ra = DirectCast(a, SendRconCommandAction)
                Dim cmd = If(ra.Command, "")
                If cmd.Length > 30 Then cmd = cmd.Substring(0, 27) & "..."
                Return $"RCON ({LookupInstanceName(ra.InstanceId)}): {cmd}"
            ElseIf TypeOf a Is NotifyAction Then
                Dim na = DirectCast(a, NotifyAction)
                Dim destName = LookupDestinationName(na.DestinationId)
                Dim msg = If(na.Message, "")
                If msg.Length > 30 Then msg = msg.Substring(0, 27) & "..."
                Return $"Notify {destName}: ""{msg}"""
            ElseIf TypeOf a Is WaitAction Then
                Dim wa = DirectCast(a, WaitAction)
                Return $"Wait {wa.DurationMs}ms"
            ElseIf TypeOf a Is WaitForReadySignalAction Then
                Dim wra = DirectCast(a, WaitForReadySignalAction)
                Dim t = If(wra.TimeoutSeconds > 0, $", {wra.TimeoutSeconds}s timeout", "")
                Return $"Wait for ready: {LookupInstanceName(wra.InstanceId)}{t}"
            ElseIf TypeOf a Is SequenceAction Then
                Dim sa = DirectCast(a, SequenceAction)
                Dim count = If(sa.Steps Is Nothing, 0, sa.Steps.Count)
                Return $"Nested sequence ({count} step{If(count = 1, "", "s")})"
            End If

            Return If(a.DisplayLabel, a.GetType().Name)
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Function GetSelectedScope() As RuleScope
            Dim selectedItem = TryCast(_scopeComboBox.SelectedItem, IdItem)
            If selectedItem Is Nothing Then Return RuleScope.Instance
            Dim val As RuleScope
            If [Enum].TryParse(selectedItem.Id, val) Then Return val
            Return RuleScope.Instance
        End Function

        Private Function GetSelectedTargetId() As String
            Dim scope = GetSelectedScope()
            If scope = RuleScope.AllInstances Then Return Nothing
            If scope = RuleScope.InstanceSet Then Return _targetComboBox.Text.Trim()
            Return GetSelectedId(_targetComboBox)
        End Function

        Friend Shared Function GetSelectedId(combo As ComboBox) As String
            Dim item = TryCast(combo.SelectedItem, IdItem)
            If item Is Nothing Then Return Nothing
            Return item.Id
        End Function

        Friend Shared Sub SelectComboById(combo As ComboBox, id As String)
            If combo Is Nothing Then Return
            If id Is Nothing Then id = ""
            For i = 0 To combo.Items.Count - 1
                Dim item = TryCast(combo.Items(i), IdItem)
                If item IsNot Nothing AndAlso
                   String.Equals(item.Id, id, StringComparison.Ordinal) Then
                    combo.SelectedIndex = i
                    Return
                End If
            Next
        End Sub

        Friend Shared Function ClampToRange(value As Integer, num As NumericUpDown) As Decimal
            If value < num.Minimum Then Return num.Minimum
            If value > num.Maximum Then Return num.Maximum
            Return value
        End Function

        Private Shared Function GetTriggerTypeId(t As ITrigger) As String
            If TypeOf t Is ScheduleTrigger Then Return "schedule"
            If TypeOf t Is StateChangeTrigger Then Return "state_change"
            If TypeOf t Is VersionMismatchTrigger Then Return "version_mismatch"
            If TypeOf t Is ManualTrigger Then Return "manual"
            Return "schedule"
        End Function

        ' ============================================================
        '  Load existing rule (edit mode)
        ' ============================================================

        Private Sub LoadExistingRule()
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim entity = db.AutomationRules.Find(_editRuleId)
                If entity Is Nothing Then
                    MessageBox.Show("Rule not found in database.", "Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error)
                    Return
                End If

                _nameTextBox.Text = If(entity.RuleName, "")
                _enabledCheckBox.Checked = entity.IsEnabled

                Dim scopeVal As RuleScope
                If [Enum].TryParse(entity.ScopeKind, True, scopeVal) Then
                    SelectComboById(_scopeComboBox, scopeVal.ToString())
                End If
                ' Same SelectedIndexChanged-doesn't-fire-on-no-op
                ' workaround we hit in 4b-1.
                OnScopeChanged()

                If String.Equals(entity.ScopeKind, RuleScope.InstanceSet.ToString(),
                                 StringComparison.OrdinalIgnoreCase) Then
                    _targetComboBox.Text = If(entity.TargetId, "")
                Else
                    SelectComboById(_targetComboBox, entity.TargetId)
                End If

                SelectComboById(_gameFilterComboBox, If(entity.GameFilter, ""))
                _overlapComboBox.SelectedIndex = 0

                ' Trigger
                Dim trigger = AutomationRuleSerializer.DeserializeTrigger(entity.TriggerJson)
                If trigger IsNot Nothing Then
                    SelectComboById(_triggerTypeCombo, GetTriggerTypeId(trigger))
                    OnTriggerTypeChanged()
                    If _currentTriggerEditor IsNot Nothing AndAlso
                       _currentTriggerEditor.LoadFn IsNot Nothing Then
                        _currentTriggerEditor.LoadFn.Invoke(trigger)
                    End If
                Else
                    OnTriggerTypeChanged()
                End If

                ' Conditions
                _conditions = AutomationRuleSerializer.DeserializeConditions(entity.ConditionsJson)
                If _conditions Is Nothing Then _conditions = New List(Of ICondition)
                RefreshConditionsList()
                _conditionModeCombo.SelectedIndex = 0

                ' Action
                Dim action = AutomationRuleSerializer.DeserializeAction(entity.ActionJson)
                If action IsNot Nothing Then
                    Dim actionTypeId = ActionEditorFactory.GetActionTypeId(action)
                    SelectComboById(_actionTypeCombo, actionTypeId)
                    OnActionTypeChanged()
                    If _currentActionEditor IsNot Nothing AndAlso
                       _currentActionEditor.LoadFn IsNot Nothing Then
                        _currentActionEditor.LoadFn.Invoke(action)
                    End If
                Else
                    OnActionTypeChanged()
                End If
            End Using
        End Sub

        ' ============================================================
        '  Save with per-tab validation glyphs
        ' ============================================================

        Private Sub OnSave(sender As Object, e As EventArgs)
            ' Reset validation glyphs from any previous Save attempt
            ' so a now-fixed problem doesn't appear stuck broken.
            ClearTabValidationGlyphs()

            ' ---- Validation: run in tab order so the "first broken
            '      tab" we autoselect is also the leftmost. Each
            '      branch marks its tab and shows the inline error,
            '      then returns.

            ' Header strip (always visible) — Name lives outside any
            ' tab. If empty, no tab to mark; just flag the field
            ' itself by focusing it.
            If String.IsNullOrWhiteSpace(_nameTextBox.Text) Then
                ShowValidationError("Rule name is required.")
                _nameTextBox.Focus()
                Return
            End If

            ' Rule tab
            Dim scopeVal = GetSelectedScope()
            Dim targetId = GetSelectedTargetId()
            If scopeVal <> RuleScope.AllInstances AndAlso String.IsNullOrEmpty(targetId) Then
                MarkTabBroken(_ruleTab)
                _tabs.SelectedTab = _ruleTab
                ShowValidationError($"Select a target {scopeVal.ToString().ToLower()}.")
                Return
            End If

            ' Trigger tab
            Dim trigger As ITrigger = Nothing
            If _currentTriggerEditor IsNot Nothing AndAlso
               _currentTriggerEditor.BuildFn IsNot Nothing Then
                trigger = _currentTriggerEditor.BuildFn.Invoke()
            End If
            If trigger Is Nothing Then
                MarkTabBroken(_triggerTab)
                _tabs.SelectedTab = _triggerTab
                ShowValidationError("Failed to build trigger.")
                Return
            End If
            Dim sched = TryCast(trigger, ScheduleTrigger)
            If sched IsNot Nothing Then
                If String.IsNullOrWhiteSpace(sched.CronExpression) Then
                    MarkTabBroken(_triggerTab)
                    _tabs.SelectedTab = _triggerTab
                    ShowValidationError("Cron expression is required for scheduled triggers.")
                    Return
                End If
                Try
                    NCrontab.CrontabSchedule.Parse(sched.CronExpression)
                Catch ex As Exception
                    MarkTabBroken(_triggerTab)
                    _tabs.SelectedTab = _triggerTab
                    ShowValidationError($"Invalid cron expression: {ex.Message}")
                    Return
                End Try
            End If

            ' Conditions tab — no validation here at the tab level;
            ' empty conditions list is valid (rule fires on every
            ' trigger match). Per-condition validation already ran
            ' inside ConditionEditorForm before any condition got
            ' added to the list.

            ' Action tab — both leaf-action and sequence cases.
            Dim action As IAction = Nothing
            If _currentActionEditor IsNot Nothing AndAlso
               _currentActionEditor.BuildFn IsNot Nothing Then
                action = _currentActionEditor.BuildFn.Invoke()
            End If
            If action Is Nothing Then
                MarkTabBroken(_actionTab)
                _tabs.SelectedTab = _actionTab
                ShowValidationError("Failed to build action.")
                Return
            End If

            Dim seqAction = TryCast(action, SequenceAction)
            If seqAction IsNot Nothing Then
                If seqAction.Steps Is Nothing OrElse seqAction.Steps.Count = 0 Then
                    MarkTabBroken(_actionTab)
                    _tabs.SelectedTab = _actionTab
                    ShowValidationError("Sequence must have at least one step. Click Add to create one, or pick a different action type.")
                    Return
                End If
                For i = 0 To seqAction.Steps.Count - 1
                    Dim stepErr = ActionEditorFactory.ValidateAction(seqAction.Steps(i))
                    If Not String.IsNullOrEmpty(stepErr) Then
                        MarkTabBroken(_actionTab)
                        _tabs.SelectedTab = _actionTab
                        ShowValidationError($"Step {i + 1}: {stepErr}")
                        Return
                    End If
                Next
            Else
                Dim actionValidationError = ActionEditorFactory.ValidateAction(action)
                If actionValidationError IsNot Nothing Then
                    MarkTabBroken(_actionTab)
                    _tabs.SelectedTab = _actionTab
                    ShowValidationError(actionValidationError)
                    Return
                End If
            End If

            ' ---- Validation passed. Resolve the remaining bits and
            '      persist.

            Dim gameFilter As String = Nothing
            Dim gameSelected = TryCast(_gameFilterComboBox.SelectedItem, IdItem)
            If gameSelected IsNot Nothing AndAlso Not String.IsNullOrEmpty(gameSelected.Id) Then
                gameFilter = gameSelected.Id
            End If

            Dim overlapVal As OverlapPolicy = OverlapPolicy.SkipIfRunning
            Dim overlapItem = TryCast(_overlapComboBox.SelectedItem, IdItem)
            If overlapItem IsNot Nothing Then
                [Enum].TryParse(overlapItem.Id, overlapVal)
            End If

            Dim conditionModeVal As ConditionMode = ConditionMode.All
            Dim modeItem = TryCast(_conditionModeCombo.SelectedItem, IdItem)
            If modeItem IsNot Nothing Then
                [Enum].TryParse(modeItem.Id, conditionModeVal)
            End If

            Dim rule As New AutomationRule With {
                .RuleId = If(_editRuleId, Guid.NewGuid().ToString("N")),
                .DisplayName = _nameTextBox.Text.Trim(),
                .IsEnabled = _enabledCheckBox.Checked,
                .Scope = scopeVal,
                .TargetId = targetId,
                .GameFilter = gameFilter,
                .Trigger = trigger,
                .Conditions = If(_conditions, New List(Of ICondition)),
                .ConditionMode = conditionModeVal,
                .Action = action,
                .Overlap = overlapVal}

            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim entity As AutomationRuleEntity
                    If String.IsNullOrEmpty(_editRuleId) Then
                        entity = AutomationEngine.SerializeRuleToEntity(rule)
                        entity.RuleId = rule.RuleId
                        entity.CreatedUtc = DateTime.UtcNow
                        entity.UpdatedUtc = DateTime.UtcNow
                        ' Place new rules at the end of the list. Existing
                        ' rules' SortOrder values are preserved by the edit
                        ' branch below — SerializeRuleToEntity(rule, entity)
                        ' overlays the rule's user-facing fields without
                        ' touching SortOrder.
                        entity.SortOrder = db.NextRuleSortOrder()
                        db.AutomationRules.Add(entity)
                    Else
                        entity = db.AutomationRules.Find(_editRuleId)
                        If entity Is Nothing Then
                            ShowValidationError("Rule no longer exists in database.") : Return
                        End If
                        AutomationEngine.SerializeRuleToEntity(rule, entity)
                        entity.UpdatedUtc = DateTime.UtcNow
                    End If
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                ShowValidationError($"Failed to save rule: {ex.Message}")
                Return
            End Try

            Dim engine = ManagerProgram.Services.GetService(Of AutomationEngine)()
            If engine IsNot Nothing Then engine.ReloadRules()

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        Private Sub ShowValidationError(msg As String)
            MessageBox.Show(msg, "Validation",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Sub

    End Class

    ' ============================================================
    '  Sub-editor record types
    ' ============================================================

    Friend Class TriggerSubEditor
        Public Property Panel As Panel
        Public Property BuildFn As Func(Of ITrigger)
        Public Property LoadFn As Action(Of ITrigger)
    End Class

    Public Class ActionSubEditor
        Public Property Panel As Panel
        Public Property BuildFn As Func(Of IAction)
        Public Property LoadFn As Action(Of IAction)
    End Class

    ''' <summary>
    ''' Lightweight item carrier for ComboBox.Items — pairs a
    ''' string identifier with a display label. ToString() returns
    ''' the display so the combo renders naturally; callers read
    ''' the Id via the GetSelectedId helper.
    ''' </summary>
    Public Class IdItem
        Public ReadOnly Property Id As String
        Public ReadOnly Property Display As String
        Public Sub New(id As String, display As String)
            Me.Id = id
            Me.Display = display
        End Sub
        Public Overrides Function ToString() As String
            Return Display
        End Function
    End Class

End Namespace
