Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports GSM.Automation
Imports GSM.Manager.Data
Imports GSM.Plugin

' ============================================================
'  ConditionEditorForm — modal editor for one condition
'
'  Phase 4b-2: small companion form to RuleEditorForm. Edits
'  one ICondition at a time across the three built-in types:
'    - InstanceStateCondition       (instance is in state X)
'    - WaitForPlayerCountCondition  (wait until <= N players)
'    - AllInstancesEmptyCondition   (multi-instance variant of above)
'
'  Same sub-editor pattern as RuleEditorForm: type combo +
'  swappable sub-panel, each with a BuildFn / LoadFn pair so
'  the form doesn't need per-type field storage.
'
'  Receives lookup data (instances, installations, nodes, set
'  tags, game IDs) from the calling RuleEditorForm via
'  constructor — avoids each open re-querying the DB and
'  keeps display names consistent between parent and child.
'
'  The TriggerSubEditor / ActionSubEditor / IdItem helper
'  classes live in RuleEditorForm.vb's namespace — we reuse
'  IdItem here for combo entries. The trigger/action editors
'  pattern is type-specific, so we define our own
'  ConditionSubEditor type below.
' ============================================================

Namespace GSM.Manager.UI

    Public Class ConditionEditorForm
        Inherits Form

        ' ---- Lookup data passed in from RuleEditorForm ----
        Private ReadOnly _instances As List(Of InstanceEntity)
        Private ReadOnly _installations As List(Of InstallationEntity)
        Private ReadOnly _nodes As List(Of NodeEntity)
        Private ReadOnly _distinctSetTags As List(Of String)
        Private ReadOnly _distinctGameIds As List(Of String)

        ' ---- The condition we're editing (Nothing for "new") ----
        Private ReadOnly _existing As ICondition

        ' ---- Result the caller reads after ShowDialog returns OK ----
        Public Property ResultCondition As ICondition

        ' ---- Controls ----
        Private _typeCombo As ComboBox
        Private _subPanel As Panel
        Private _currentEditor As ConditionSubEditor

        Public Sub New(instances As List(Of InstanceEntity),
                       installations As List(Of InstallationEntity),
                       nodes As List(Of NodeEntity),
                       distinctSetTags As List(Of String),
                       distinctGameIds As List(Of String),
                       Optional existing As ICondition = Nothing)
            FormIconHelper.ApplyTo(Me)
            _instances = If(instances, New List(Of InstanceEntity))
            _installations = If(installations, New List(Of InstallationEntity))
            _nodes = If(nodes, New List(Of NodeEntity))
            _distinctSetTags = If(distinctSetTags, New List(Of String))
            _distinctGameIds = If(distinctGameIds, New List(Of String))
            _existing = existing
            InitializeControls()
            If _existing IsNot Nothing Then LoadExisting()
        End Sub

        ' ============================================================
        '  Layout
        ' ============================================================

        Private Sub InitializeControls()
            Me.Text = If(_existing Is Nothing, "Add Condition", "Edit Condition")
            ' Form height bumped 360 → 400 for proper bottom
            ' padding around the Save/Cancel buttons. Without this
            ' the buttons visibly hugged the form's bottom edge —
            ' the client area (form height minus title bar ~30px)
            ' was 330px, with buttons at y=290+32 = 322, leaving
            ' only ~8px of breathing room.
            Me.Size = New Size(640, 400)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim grp As New GroupBox() With {
                .Text = "Condition",
                .Location = New Point(15, 10),
                .Size = New Size(600, 270),
                .Font = New Font("Segoe UI", 9, FontStyle.Bold)}
            Me.Controls.Add(grp)

            Dim contentFont As New Font("Segoe UI", 9)

            Dim typeLbl As New Label() With {
                .Text = "Type:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, 28)}
            grp.Controls.Add(typeLbl)
            _typeCombo = New ComboBox() With {
                .Location = New Point(60, 25), .Size = New Size(280, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            _typeCombo.Items.AddRange(New Object() {
                New IdItem("instance_state", "Instance State Check"),
                New IdItem("wait_player_count", "Wait For Player Count"),
                New IdItem("all_instances_empty", "All Instances Empty (multi-instance)")})
            _typeCombo.SelectedIndex = 0
            AddHandler _typeCombo.SelectedIndexChanged, Sub(s, e) OnTypeChanged()
            grp.Controls.Add(_typeCombo)

            _subPanel = New Panel() With {
                .Location = New Point(15, 60),
                .Size = New Size(570, 200),
                .BackColor = SystemColors.Window,
                .BorderStyle = BorderStyle.FixedSingle}
            grp.Controls.Add(_subPanel)

            ' Buttons — y=325 sits ~12px above the client area
            ' bottom (form 400 − title bar 30 − button height 32 −
            ' margin 13 ≈ 325). Matches the visual padding pattern
            ' on RuleEditorForm and other modals.
            Dim saveBtn As New Button() With {
                .Text = "Save", .Size = New Size(100, 32),
                .Location = New Point(405, 325)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)
            Me.AcceptButton = saveBtn

            Dim cancelBtn As New Button() With {
                .Text = "Cancel", .Size = New Size(100, 32),
                .Location = New Point(515, 325),
                .DialogResult = DialogResult.Cancel}
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn

            ' Mount the initial sub-editor for whatever type is
            ' selected (Instance State Check by default).
            OnTypeChanged()
        End Sub

        ' ============================================================
        '  Sub-editor swap
        ' ============================================================

        Private Sub OnTypeChanged()
            _subPanel.Controls.Clear()
            Dim selectedItem = TryCast(_typeCombo.SelectedItem, IdItem)
            Dim id As String = If(selectedItem IsNot Nothing, selectedItem.Id, "instance_state")
            Select Case id
                Case "instance_state" : _currentEditor = BuildInstanceStateEditor()
                Case "wait_player_count" : _currentEditor = BuildWaitForPlayerCountEditor()
                Case "all_instances_empty" : _currentEditor = BuildAllInstancesEmptyEditor()
                Case Else : _currentEditor = BuildInstanceStateEditor()
            End Select
            If _currentEditor IsNot Nothing AndAlso _currentEditor.Panel IsNot Nothing Then
                _currentEditor.Panel.Dock = DockStyle.Fill
                _subPanel.Controls.Add(_currentEditor.Panel)
            End If
        End Sub

        ' ============================================================
        '  Sub-editor builders
        '
        '  CType cast pattern is the same as RuleEditorForm — VB
        '  lambda return-type inference produces Func(Of Concrete)
        '  which doesn't fit Func(Of ICondition). See gotcha table.
        ' ============================================================

        Private Function BuildInstanceStateEditor() As ConditionSubEditor
            Dim panel As New Panel()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim instLbl As New Label() With {
                .Text = "Instance:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 13)}
            panel.Controls.Add(instLbl)
            Dim instCombo As New ComboBox() With {
                .Location = New Point(120, 10), .Size = New Size(420, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            For Each inst In _instances.OrderBy(Function(i) i.DisplayName)
                instCombo.Items.Add(New IdItem(inst.InstanceId,
                                                $"{inst.DisplayName} ({inst.GameId})"))
            Next
            panel.Controls.Add(instCombo)

            Dim stateLbl As New Label() With {
                .Text = "Required state:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 43)}
            panel.Controls.Add(stateLbl)
            Dim stateCombo As New ComboBox() With {
                .Location = New Point(120, 40), .Size = New Size(200, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            For Each st In [Enum].GetValues(GetType(InstanceState))
                stateCombo.Items.Add(New IdItem(st.ToString(), st.ToString()))
            Next
            stateCombo.SelectedIndex = 0
            panel.Controls.Add(stateCombo)

            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 80), .MaximumSize = New Size(540, 0),
                .Text = "Passes immediately if the instance is in the required state, fails otherwise. Use this to gate actions on a specific state (e.g. ""only restart if currently Running"")."}
            panel.Controls.Add(help)

            Return New ConditionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() As ICondition
                               Dim stateItem = TryCast(stateCombo.SelectedItem, IdItem)
                               Dim stateVal As InstanceState = InstanceState.Stopped
                               If stateItem IsNot Nothing Then
                                   [Enum].TryParse(stateItem.Id, stateVal)
                               End If
                               Return New InstanceStateCondition With {
                                   .InstanceId = GetSelectedId(instCombo),
                                   .RequiredState = stateVal}
                           End Function,
                .LoadFn = Sub(c)
                              Dim ic = TryCast(c, InstanceStateCondition)
                              If ic Is Nothing Then Return
                              SelectComboById(instCombo, ic.InstanceId)
                              SelectComboById(stateCombo, ic.RequiredState.ToString())
                          End Sub,
                .ValidateFn = Function() As String
                                  If String.IsNullOrEmpty(GetSelectedId(instCombo)) Then
                                      Return "Select an instance."
                                  End If
                                  Return Nothing
                              End Function}
        End Function

        Private Function BuildWaitForPlayerCountEditor() As ConditionSubEditor
            Dim panel As New Panel()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim instLbl As New Label() With {
                .Text = "Instance:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 13)}
            panel.Controls.Add(instLbl)
            Dim instCombo As New ComboBox() With {
                .Location = New Point(140, 10), .Size = New Size(400, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            For Each inst In _instances.OrderBy(Function(i) i.DisplayName)
                instCombo.Items.Add(New IdItem(inst.InstanceId,
                                                $"{inst.DisplayName} ({inst.GameId})"))
            Next
            panel.Controls.Add(instCombo)

            Dim maxLbl As New Label() With {
                .Text = "Max players (≤):", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 43)}
            panel.Controls.Add(maxLbl)
            Dim maxNum As New NumericUpDown() With {
                .Location = New Point(140, 40), .Size = New Size(80, 24),
                .Minimum = 0, .Maximum = 1000, .Value = 0, .Font = contentFont}
            panel.Controls.Add(maxNum)

            Dim pollLbl As New Label() With {
                .Text = "Poll interval (ms):", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 73)}
            panel.Controls.Add(pollLbl)
            Dim pollNum As New NumericUpDown() With {
                .Location = New Point(140, 70), .Size = New Size(80, 24),
                .Minimum = 1000, .Maximum = 300000, .Increment = 1000,
                .Value = 15000, .Font = contentFont}
            panel.Controls.Add(pollNum)

            Dim toLbl As New Label() With {
                .Text = "Timeout (ms, 0=∞):", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 103)}
            panel.Controls.Add(toLbl)
            Dim toNum As New NumericUpDown() With {
                .Location = New Point(140, 100), .Size = New Size(100, 24),
                .Minimum = 0, .Maximum = 3600000, .Increment = 1000,
                .Value = 0, .Font = contentFont}
            panel.Controls.Add(toNum)

            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 140), .MaximumSize = New Size(540, 0),
                .Text = "Polls until the player count is at or below the threshold. Times out as a fail (0 = wait indefinitely)."}
            panel.Controls.Add(help)

            Return New ConditionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New WaitForPlayerCountCondition With {
                    .InstanceId = GetSelectedId(instCombo),
                    .MaxPlayers = CInt(maxNum.Value),
                    .PollIntervalMs = CInt(pollNum.Value),
                    .TimeoutMs = CInt(toNum.Value)}, ICondition),
                .LoadFn = Sub(c)
                              Dim wc = TryCast(c, WaitForPlayerCountCondition)
                              If wc Is Nothing Then Return
                              SelectComboById(instCombo, wc.InstanceId)
                              maxNum.Value = ClampToRange(wc.MaxPlayers, maxNum)
                              pollNum.Value = ClampToRange(wc.PollIntervalMs, pollNum)
                              toNum.Value = ClampToRange(wc.TimeoutMs, toNum)
                          End Sub,
                .ValidateFn = Function() As String
                                  If String.IsNullOrEmpty(GetSelectedId(instCombo)) Then
                                      Return "Select an instance."
                                  End If
                                  Return Nothing
                              End Function}
        End Function

        Private Function BuildAllInstancesEmptyEditor() As ConditionSubEditor
            Dim panel As New Panel()
            Dim contentFont As New Font("Segoe UI", 9)

            ' Scope picker — Instance excluded (use WaitForPlayerCount
            ' instead for single-instance). AllInstances is included
            ' because it's a valid global "no players anywhere" gate.
            Dim scopeLbl As New Label() With {
                .Text = "Scope:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 13)}
            panel.Controls.Add(scopeLbl)
            Dim scopeCombo As New ComboBox() With {
                .Location = New Point(140, 10), .Size = New Size(180, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            scopeCombo.Items.AddRange(New Object() {
                New IdItem(RuleScope.Installation.ToString(), "Installation"),
                New IdItem(RuleScope.Node.ToString(), "Node"),
                New IdItem(RuleScope.InstanceSet.ToString(), "Instance Set"),
                New IdItem(RuleScope.AllInstances.ToString(), "All Instances")})
            scopeCombo.SelectedIndex = 0
            panel.Controls.Add(scopeCombo)

            ' Target — varies by scope, populated/cleared in handler.
            Dim targetLbl As New Label() With {
                .Text = "Target:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 43)}
            panel.Controls.Add(targetLbl)
            Dim targetCombo As New ComboBox() With {
                .Location = New Point(140, 40), .Size = New Size(400, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            panel.Controls.Add(targetCombo)

            ' GameFilter combo — always visible, defaults to (any).
            Dim gameLbl As New Label() With {
                .Text = "Game filter:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 73)}
            panel.Controls.Add(gameLbl)
            Dim gameCombo As New ComboBox() With {
                .Location = New Point(140, 70), .Size = New Size(180, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            gameCombo.Items.Add(New IdItem("", "(any game)"))
            For Each gid In _distinctGameIds
                gameCombo.Items.Add(New IdItem(gid, gid))
            Next
            gameCombo.SelectedIndex = 0
            panel.Controls.Add(gameCombo)

            ' Player count + timing controls — single row to save space.
            Dim maxLbl As New Label() With {
                .Text = "Max players:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 105)}
            panel.Controls.Add(maxLbl)
            Dim maxNum As New NumericUpDown() With {
                .Location = New Point(140, 102), .Size = New Size(60, 24),
                .Minimum = 0, .Maximum = 1000, .Value = 0, .Font = contentFont}
            panel.Controls.Add(maxNum)

            Dim pollLbl As New Label() With {
                .Text = "Poll (ms):", .AutoSize = True, .Font = contentFont,
                .Location = New Point(220, 105)}
            panel.Controls.Add(pollLbl)
            Dim pollNum As New NumericUpDown() With {
                .Location = New Point(290, 102), .Size = New Size(80, 24),
                .Minimum = 1000, .Maximum = 300000, .Increment = 1000,
                .Value = 15000, .Font = contentFont}
            panel.Controls.Add(pollNum)

            Dim toLbl As New Label() With {
                .Text = "Timeout (ms):", .AutoSize = True, .Font = contentFont,
                .Location = New Point(390, 105)}
            panel.Controls.Add(toLbl)
            Dim toNum As New NumericUpDown() With {
                .Location = New Point(470, 102), .Size = New Size(80, 24),
                .Minimum = 0, .Maximum = 3600000, .Increment = 1000,
                .Value = 0, .Font = contentFont}
            panel.Controls.Add(toNum)

            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 140), .MaximumSize = New Size(540, 0),
                .Text = "Polls until ALL instances in the chosen scope are at or below the player threshold. Game filter narrows the set when scope spans multiple games."}
            panel.Controls.Add(help)

            ' Scope change repopulates the target combo. Same logic
            ' as RuleEditorForm.OnScopeChanged but scoped to this
            ' editor — duplicated rather than abstracted because
            ' factoring it out for two callsites isn't worth a
            ' shared helper class. AllInstances scope hides the
            ' target row entirely (no per-target selection needed).
            Dim repopulateTarget As Action =
                Sub()
                    targetCombo.Items.Clear()
                    targetCombo.Text = ""
                    Dim scopeItem = TryCast(scopeCombo.SelectedItem, IdItem)
                    Dim scopeStr As String = If(scopeItem IsNot Nothing, scopeItem.Id, "")
                    Dim scopeVal As RuleScope = RuleScope.Installation
                    [Enum].TryParse(scopeStr, scopeVal)

                    Select Case scopeVal
                        Case RuleScope.Installation
                            targetCombo.DropDownStyle = ComboBoxStyle.DropDownList
                            targetLbl.Visible = True
                            targetCombo.Visible = True
                            For Each ins In _installations.OrderBy(Function(i) i.DisplayName)
                                targetCombo.Items.Add(New IdItem(ins.InstallationId,
                                                                  $"{ins.DisplayName} ({ins.GameId})"))
                            Next

                        Case RuleScope.Node
                            targetCombo.DropDownStyle = ComboBoxStyle.DropDownList
                            targetLbl.Visible = True
                            targetCombo.Visible = True
                            For Each n In _nodes.OrderBy(Function(x) x.DisplayName)
                                targetCombo.Items.Add(New IdItem(n.NodeId,
                                                                  $"{n.DisplayName} ({n.HostAddress}:{n.Port})"))
                            Next

                        Case RuleScope.InstanceSet
                            targetCombo.DropDownStyle = ComboBoxStyle.DropDown
                            targetCombo.AutoCompleteMode = AutoCompleteMode.SuggestAppend
                            targetCombo.AutoCompleteSource = AutoCompleteSource.ListItems
                            targetLbl.Visible = True
                            targetCombo.Visible = True
                            ' Loop variable named `setTag` not `tag` —
                            ' Form.Tag inheritance gotcha (see RuleEditorForm).
                            For Each setTag In _distinctSetTags
                                targetCombo.Items.Add(setTag)
                            Next

                        Case RuleScope.AllInstances
                            targetLbl.Visible = False
                            targetCombo.Visible = False
                    End Select
                End Sub
            AddHandler scopeCombo.SelectedIndexChanged, Sub(s, e) repopulateTarget()
            repopulateTarget()  ' initial fill for default scope

            Return New ConditionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() As ICondition
                               Dim scopeItem = TryCast(scopeCombo.SelectedItem, IdItem)
                               Dim scopeVal As RuleScope = RuleScope.Installation
                               If scopeItem IsNot Nothing Then
                                   [Enum].TryParse(scopeItem.Id, scopeVal)
                               End If

                               ' Resolve target by scope. AllInstances has
                               ' no target. InstanceSet uses raw text.
                               ' Others use IdItem lookups.
                               Dim targetVal As String = Nothing
                               If scopeVal = RuleScope.InstanceSet Then
                                   targetVal = targetCombo.Text.Trim()
                               ElseIf scopeVal <> RuleScope.AllInstances Then
                                   targetVal = GetSelectedId(targetCombo)
                               End If

                               Dim gameItem = TryCast(gameCombo.SelectedItem, IdItem)
                               Dim gameVal As String = Nothing
                               If gameItem IsNot Nothing AndAlso Not String.IsNullOrEmpty(gameItem.Id) Then
                                   gameVal = gameItem.Id
                               End If

                               Return New AllInstancesEmptyCondition With {
                                   .Scope = scopeVal,
                                   .TargetId = targetVal,
                                   .GameFilter = gameVal,
                                   .MaxPlayers = CInt(maxNum.Value),
                                   .PollIntervalMs = CInt(pollNum.Value),
                                   .TimeoutMs = CInt(toNum.Value)}
                           End Function,
                .LoadFn = Sub(c)
                              Dim ac = TryCast(c, AllInstancesEmptyCondition)
                              If ac Is Nothing Then Return
                              SelectComboById(scopeCombo, ac.Scope.ToString())
                              ' SelectComboById on the same value as
                              ' the default doesn't fire SelectedIndexChanged
                              ' (same WinForms gotcha as RuleEditorForm hit),
                              ' so call repopulate explicitly to make sure
                              ' the target combo is populated for the
                              ' loaded scope before we try to select an item.
                              repopulateTarget()
                              If ac.Scope = RuleScope.InstanceSet Then
                                  targetCombo.Text = If(ac.TargetId, "")
                              ElseIf ac.Scope <> RuleScope.AllInstances Then
                                  SelectComboById(targetCombo, ac.TargetId)
                              End If
                              SelectComboById(gameCombo, If(ac.GameFilter, ""))
                              maxNum.Value = ClampToRange(ac.MaxPlayers, maxNum)
                              pollNum.Value = ClampToRange(ac.PollIntervalMs, pollNum)
                              toNum.Value = ClampToRange(ac.TimeoutMs, toNum)
                          End Sub,
                .ValidateFn = Function() As String
                                  Dim scopeItem = TryCast(scopeCombo.SelectedItem, IdItem)
                                  Dim scopeVal As RuleScope = RuleScope.Installation
                                  If scopeItem IsNot Nothing Then
                                      [Enum].TryParse(scopeItem.Id, scopeVal)
                                  End If
                                  If scopeVal = RuleScope.AllInstances Then Return Nothing
                                  Dim hasTarget As Boolean
                                  If scopeVal = RuleScope.InstanceSet Then
                                      hasTarget = Not String.IsNullOrWhiteSpace(targetCombo.Text)
                                  Else
                                      hasTarget = Not String.IsNullOrEmpty(GetSelectedId(targetCombo))
                                  End If
                                  If Not hasTarget Then
                                      Return $"Select a target {scopeVal.ToString().ToLower()}."
                                  End If
                                  Return Nothing
                              End Function}
        End Function

        ' ============================================================
        '  Load existing condition into the appropriate sub-editor
        ' ============================================================

        Private Sub LoadExisting()
            If _existing Is Nothing Then Return
            Dim typeId As String
            If TypeOf _existing Is InstanceStateCondition Then
                typeId = "instance_state"
            ElseIf TypeOf _existing Is WaitForPlayerCountCondition Then
                typeId = "wait_player_count"
            ElseIf TypeOf _existing Is AllInstancesEmptyCondition Then
                typeId = "all_instances_empty"
            Else
                ' Unknown condition type (plugin-contributed?) —
                ' leave the form on default state, save will produce
                ' a fresh InstanceStateCondition. User can cancel
                ' if they want to preserve the unknown one.
                Return
            End If

            SelectComboById(_typeCombo, typeId)
            ' SelectComboById is a no-op when the target index already
            ' matches the current SelectedIndex (e.g. instance_state,
            ' position 0, is the default). Call OnTypeChanged
            ' explicitly to guarantee the right sub-editor is mounted.
            ' Same WinForms gotcha as RuleEditorForm. Idempotent.
            OnTypeChanged()
            If _currentEditor IsNot Nothing AndAlso _currentEditor.LoadFn IsNot Nothing Then
                _currentEditor.LoadFn.Invoke(_existing)
            End If
        End Sub

        ' ============================================================
        '  Save
        ' ============================================================

        Private Sub OnSave(sender As Object, e As EventArgs)
            If _currentEditor Is Nothing OrElse _currentEditor.BuildFn Is Nothing Then
                MessageBox.Show("No condition editor active.", "Validation",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ' Per-type validation runs first; bail with the message
            ' before constructing the condition object.
            If _currentEditor.ValidateFn IsNot Nothing Then
                Dim err = _currentEditor.ValidateFn.Invoke()
                If Not String.IsNullOrEmpty(err) Then
                    MessageBox.Show(err, "Validation",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
            End If

            ResultCondition = _currentEditor.BuildFn.Invoke()
            If ResultCondition Is Nothing Then
                MessageBox.Show("Failed to build condition.", "Validation",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        ' ============================================================
        '  Helpers — duplicated from RuleEditorForm rather than
        '  exposed as Friend Shared on that class. Reasoning: keeps
        '  the form's surface area private. The few-line cost of
        '  duplication is lower than the maintenance cost of an
        '  inter-form helper API.
        ' ============================================================

        Private Shared Function GetSelectedId(combo As ComboBox) As String
            Dim item = TryCast(combo.SelectedItem, IdItem)
            If item Is Nothing Then Return Nothing
            Return item.Id
        End Function

        Private Shared Sub SelectComboById(combo As ComboBox, id As String)
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

        Private Shared Function ClampToRange(value As Integer, num As NumericUpDown) As Decimal
            If value < num.Minimum Then Return num.Minimum
            If value > num.Maximum Then Return num.Maximum
            Return value
        End Function

    End Class

    ' ============================================================
    '  ConditionSubEditor — sibling to TriggerSubEditor /
    '  ActionSubEditor (defined in RuleEditorForm.vb). Includes a
    '  ValidateFn slot because conditions have varying validation
    '  needs and centralising it in OnSave (like RuleEditorForm
    '  does for actions) would require an enum dispatch on type
    '  — cleaner to colocate validation with the sub-editor that
    '  knows its own controls.
    ' ============================================================

    Friend Class ConditionSubEditor
        Public Property Panel As Panel
        Public Property BuildFn As Func(Of ICondition)
        Public Property LoadFn As Action(Of ICondition)
        Public Property ValidateFn As Func(Of String)
    End Class

End Namespace
