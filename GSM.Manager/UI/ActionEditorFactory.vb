Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports GSM.Automation
Imports GSM.Manager.Data
Imports GSM.Plugin

' ============================================================
'  ActionEditorFactory — builds per-action sub-editors
'
'  Phase 4b-3: extracted from RuleEditorForm so both the rule
'  editor and the new StepEditorForm (modal for editing a step
'  inside a SequenceAction) can share the same set of action
'  sub-editors. Without this, both forms would either need to
'  duplicate ~250 lines of builder code, or RuleEditorForm
'  would need to expose its private builders.
'
'  The factory holds the lookup data (instances, installations,
'  notification destinations) so builders' lambdas can close
'  over the factory instance instead of a specific Form. Each
'  caller form constructs its own factory at form-construction
'  time using the lookup data it already loaded.
'
'  The 11 leaf-action builders live here. SequenceAction is
'  intentionally excluded — it's authored via the parent form's
'  step-list UI, not as a sub-editor inside another sequence
'  (no nested-sequence UI; the serializer supports nesting but
'  the UX gets unwieldy fast).
'
'  Sub-editor pattern (unchanged from 4b-1):
'    - Each builder creates a Panel and populates it
'    - Returns ActionSubEditor { Panel, BuildFn, LoadFn }
'    - Lambdas close over the panel's controls, not over the
'      factory's fields, so the factory can be GC'd as soon as
'      the form's last sub-editor instance is disposed
'
'  Lambda return type quirk: VB.Net infers concrete types from
'  the lambda body. Lambdas constructing a concrete IAction get
'  Func(Of ConcreteType), which doesn't fit Func(Of IAction).
'  Two workarounds (both in use here):
'    - Single-expression: wrap in CType(..., IAction)
'    - Multi-line/branching: explicit `Function() As IAction`
' ============================================================

Namespace GSM.Manager.UI

    Public Class ActionEditorFactory

        Private ReadOnly _instances As IReadOnlyList(Of InstanceEntity)
        Private ReadOnly _installations As IReadOnlyList(Of InstallationEntity)
        Private ReadOnly _notificationDestinations As IReadOnlyList(Of NotificationDestinationEntity)

        Public Sub New(instances As IReadOnlyList(Of InstanceEntity),
                       installations As IReadOnlyList(Of InstallationEntity),
                       notificationDestinations As IReadOnlyList(Of NotificationDestinationEntity))
            _instances = If(instances, CType(New List(Of InstanceEntity), IReadOnlyList(Of InstanceEntity)))
            _installations = If(installations, CType(New List(Of InstallationEntity), IReadOnlyList(Of InstallationEntity)))
            _notificationDestinations = If(notificationDestinations,
                                            CType(New List(Of NotificationDestinationEntity), IReadOnlyList(Of NotificationDestinationEntity)))
        End Sub

        ' ============================================================
        '  Public dispatcher
        ' ============================================================

        ''' <summary>
        ''' Build the sub-editor for a given action-id discriminator.
        ''' Falls back to CoordinatedRestart for unknown ids — keeps
        ''' the form usable rather than crashing on a bad rule.
        ''' </summary>
        Public Function BuildEditor(id As String) As ActionSubEditor
            Select Case id
                Case "coordinated_restart" : Return BuildCoordinatedRestartEditor()
                Case "start_instance" : Return BuildStartInstanceEditor()
                Case "stop_instance" : Return BuildStopInstanceEditor()
                Case "restart_instance" : Return BuildRestartInstanceEditor()
                Case "start_all_instances" : Return BuildStartAllInstancesEditor()
                Case "stop_all_instances" : Return BuildStopAllInstancesEditor()
                Case "update_installation" : Return BuildUpdateInstallationEditor()
                Case "send_rcon" : Return BuildSendRconEditor()
                Case "notify" : Return BuildNotifyEditor()
                Case "wait" : Return BuildWaitEditor()
                Case "wait_for_ready" : Return BuildWaitForReadyEditor()
                Case Else : Return BuildCoordinatedRestartEditor()
            End Select
        End Function

        ''' <summary>
        ''' Map a concrete IAction back to its action-id string.
        ''' Mirror of BuildEditor's switch.
        ''' </summary>
        Public Shared Function GetActionTypeId(a As IAction) As String
            If a Is Nothing Then Return "coordinated_restart"
            If TypeOf a Is CoordinatedRestartAction Then Return "coordinated_restart"
            If TypeOf a Is StartInstanceAction Then Return "start_instance"
            If TypeOf a Is StopInstanceAction Then Return "stop_instance"
            If TypeOf a Is RestartInstanceAction Then Return "restart_instance"
            If TypeOf a Is StartAllInstancesAction Then Return "start_all_instances"
            If TypeOf a Is StopAllInstancesAction Then Return "stop_all_instances"
            If TypeOf a Is UpdateInstallationAction Then Return "update_installation"
            If TypeOf a Is SendRconCommandAction Then Return "send_rcon"
            If TypeOf a Is NotifyAction Then Return "notify"
            If TypeOf a Is WaitAction Then Return "wait"
            If TypeOf a Is WaitForReadySignalAction Then Return "wait_for_ready"
            If TypeOf a Is SequenceAction Then Return "sequence"
            Return "coordinated_restart"
        End Function

        ''' <summary>
        ''' Per-action-type validation. Returns null if OK, or an
        ''' error message if a required field is missing. Pulled
        ''' from RuleEditorForm so StepEditorForm can run the same
        ''' rules without re-implementing.
        ''' </summary>
        Public Shared Function ValidateAction(action As IAction) As String
            If action Is Nothing Then Return "No action."
            If TypeOf action Is StartInstanceAction Then
                If String.IsNullOrEmpty(DirectCast(action, StartInstanceAction).InstanceId) Then
                    Return "Select an instance."
                End If
            ElseIf TypeOf action Is StopInstanceAction Then
                If String.IsNullOrEmpty(DirectCast(action, StopInstanceAction).InstanceId) Then
                    Return "Select an instance."
                End If
            ElseIf TypeOf action Is RestartInstanceAction Then
                If String.IsNullOrEmpty(DirectCast(action, RestartInstanceAction).InstanceId) Then
                    Return "Select an instance."
                End If
            ElseIf TypeOf action Is CoordinatedRestartAction Then
                If String.IsNullOrEmpty(DirectCast(action, CoordinatedRestartAction).InstanceId) Then
                    Return "Select an instance."
                End If
            ElseIf TypeOf action Is StartAllInstancesAction Then
                If String.IsNullOrEmpty(DirectCast(action, StartAllInstancesAction).InstallationId) Then
                    Return "Select an installation."
                End If
            ElseIf TypeOf action Is StopAllInstancesAction Then
                If String.IsNullOrEmpty(DirectCast(action, StopAllInstancesAction).InstallationId) Then
                    Return "Select an installation."
                End If
            ElseIf TypeOf action Is UpdateInstallationAction Then
                If String.IsNullOrEmpty(DirectCast(action, UpdateInstallationAction).InstallationId) Then
                    Return "Select an installation."
                End If
            ElseIf TypeOf action Is SendRconCommandAction Then
                Dim ra = DirectCast(action, SendRconCommandAction)
                If String.IsNullOrEmpty(ra.InstanceId) Then Return "Select an instance."
                If String.IsNullOrWhiteSpace(ra.Command) Then Return "RCON command is required."
            ElseIf TypeOf action Is NotifyAction Then
                Dim na = DirectCast(action, NotifyAction)
                If String.IsNullOrEmpty(na.DestinationId) Then Return "Select a notification destination."
                If String.IsNullOrWhiteSpace(na.Message) Then Return "Notification message is required."
            ElseIf TypeOf action Is WaitForReadySignalAction Then
                If String.IsNullOrEmpty(DirectCast(action, WaitForReadySignalAction).InstanceId) Then
                    Return "Select an instance."
                End If
            End If
            ' WaitAction: DurationMs has a sensible default
            ' SequenceAction: validated by the parent form's step-list logic
            Return Nothing
        End Function

        ' ============================================================
        '  Common row builders
        ' ============================================================

        Private Function AddInstanceComboRow(panel As Panel, y As Integer,
                                              labelText As String) As ComboBox
            Dim contentFont As New Font("Segoe UI", 9)
            Dim lbl As New Label() With {
                .Text = labelText, .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, y + 3)}
            panel.Controls.Add(lbl)
            Dim combo As New ComboBox() With {
                .Location = New Point(120, y), .Size = New Size(440, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            For Each inst In _instances.OrderBy(Function(i) i.DisplayName)
                combo.Items.Add(New IdItem(inst.InstanceId,
                                            $"{inst.DisplayName} ({inst.GameId})"))
            Next
            panel.Controls.Add(combo)
            Return combo
        End Function

        Private Function AddInstallationComboRow(panel As Panel, y As Integer,
                                                  labelText As String) As ComboBox
            Dim contentFont As New Font("Segoe UI", 9)
            Dim lbl As New Label() With {
                .Text = labelText, .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, y + 3)}
            panel.Controls.Add(lbl)
            Dim combo As New ComboBox() With {
                .Location = New Point(120, y), .Size = New Size(440, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            For Each ins In _installations.OrderBy(Function(i) i.DisplayName)
                combo.Items.Add(New IdItem(ins.InstallationId,
                                            $"{ins.DisplayName} ({ins.GameId})"))
            Next
            panel.Controls.Add(combo)
            Return combo
        End Function

        Private Function AddNumericRow(panel As Panel, y As Integer,
                                        labelText As String,
                                        unitText As String,
                                        minVal As Integer, maxVal As Integer,
                                        defaultVal As Integer) As NumericUpDown
            Dim contentFont As New Font("Segoe UI", 9)
            Dim lbl As New Label() With {
                .Text = labelText, .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, y + 3)}
            panel.Controls.Add(lbl)
            Dim num As New NumericUpDown() With {
                .Location = New Point(160, y), .Size = New Size(100, 24),
                .Minimum = minVal, .Maximum = maxVal, .Value = defaultVal,
                .Font = contentFont}
            panel.Controls.Add(num)
            If Not String.IsNullOrEmpty(unitText) Then
                Dim unit As New Label() With {
                    .Text = unitText, .AutoSize = True, .Font = contentFont,
                    .ForeColor = Color.FromArgb(100, 100, 100),
                    .Location = New Point(265, y + 3)}
                panel.Controls.Add(unit)
            End If
            Return num
        End Function

        Private Function AddTextBoxRow(panel As Panel, y As Integer,
                                        labelText As String,
                                        Optional widthVal As Integer = 440) As TextBox
            Dim contentFont As New Font("Segoe UI", 9)
            Dim lbl As New Label() With {
                .Text = labelText, .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, y + 3)}
            panel.Controls.Add(lbl)
            Dim txt As New TextBox() With {
                .Location = New Point(120, y), .Size = New Size(widthVal, 24),
                .Font = contentFont}
            panel.Controls.Add(txt)
            Return txt
        End Function

        ' ============================================================
        '  Action editor builders (the 11 leaves)
        ' ============================================================

        Private Function BuildCoordinatedRestartEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim instanceCombo = AddInstanceComboRow(panel, 10, "Instance:")
            Dim gracefulNum = AddNumericRow(panel, 40, "Graceful timeout:", "ms", 0, 600000, 10000)
            Dim delayNum = AddNumericRow(panel, 70, "Delay between:", "ms", 0, 60000, 2000)
            Dim readyNum = AddNumericRow(panel, 100, "Ready timeout:", "s (0=plugin default)", 0, 3600, 0)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New CoordinatedRestartAction With {
                    .InstanceId = RuleEditorForm.GetSelectedId(instanceCombo),
                    .GracefulTimeoutMs = CInt(gracefulNum.Value),
                    .DelayBetweenMs = CInt(delayNum.Value),
                    .ReadyTimeoutSeconds = CInt(readyNum.Value)}, IAction),
                .LoadFn = Sub(a)
                              Dim ca = TryCast(a, CoordinatedRestartAction)
                              If ca Is Nothing Then Return
                              RuleEditorForm.SelectComboById(instanceCombo, ca.InstanceId)
                              gracefulNum.Value = RuleEditorForm.ClampToRange(ca.GracefulTimeoutMs, gracefulNum)
                              delayNum.Value = RuleEditorForm.ClampToRange(ca.DelayBetweenMs, delayNum)
                              readyNum.Value = RuleEditorForm.ClampToRange(ca.ReadyTimeoutSeconds, readyNum)
                          End Sub}
        End Function

        Private Function BuildStartInstanceEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim instanceCombo = AddInstanceComboRow(panel, 10, "Instance:")
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New StartInstanceAction With {
                    .InstanceId = RuleEditorForm.GetSelectedId(instanceCombo)}, IAction),
                .LoadFn = Sub(a)
                              Dim sa = TryCast(a, StartInstanceAction)
                              If sa IsNot Nothing Then RuleEditorForm.SelectComboById(instanceCombo, sa.InstanceId)
                          End Sub}
        End Function

        Private Function BuildStopInstanceEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim instanceCombo = AddInstanceComboRow(panel, 10, "Instance:")
            Dim gracefulNum = AddNumericRow(panel, 40, "Graceful timeout:", "ms", 0, 600000, 10000)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New StopInstanceAction With {
                    .InstanceId = RuleEditorForm.GetSelectedId(instanceCombo),
                    .GracefulTimeoutMs = CInt(gracefulNum.Value)}, IAction),
                .LoadFn = Sub(a)
                              Dim sa = TryCast(a, StopInstanceAction)
                              If sa Is Nothing Then Return
                              RuleEditorForm.SelectComboById(instanceCombo, sa.InstanceId)
                              gracefulNum.Value = RuleEditorForm.ClampToRange(sa.GracefulTimeoutMs, gracefulNum)
                          End Sub}
        End Function

        Private Function BuildRestartInstanceEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim instanceCombo = AddInstanceComboRow(panel, 10, "Instance:")
            Dim gracefulNum = AddNumericRow(panel, 40, "Graceful timeout:", "ms", 0, 600000, 10000)
            Dim delayNum = AddNumericRow(panel, 70, "Delay between:", "ms", 0, 60000, 2000)
            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(150, 100, 30),
                .Location = New Point(10, 100), .MaximumSize = New Size(660, 0),
                .Text = "Note: this is a basic restart. For multi-instance installs, use Coordinated Restart so restarts queue rather than running simultaneously."}
            panel.Controls.Add(help)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New RestartInstanceAction With {
                    .InstanceId = RuleEditorForm.GetSelectedId(instanceCombo),
                    .GracefulTimeoutMs = CInt(gracefulNum.Value),
                    .DelayBetweenMs = CInt(delayNum.Value)}, IAction),
                .LoadFn = Sub(a)
                              Dim ra = TryCast(a, RestartInstanceAction)
                              If ra Is Nothing Then Return
                              RuleEditorForm.SelectComboById(instanceCombo, ra.InstanceId)
                              gracefulNum.Value = RuleEditorForm.ClampToRange(ra.GracefulTimeoutMs, gracefulNum)
                              delayNum.Value = RuleEditorForm.ClampToRange(ra.DelayBetweenMs, delayNum)
                          End Sub}
        End Function

        Private Function BuildStartAllInstancesEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim installCombo = AddInstallationComboRow(panel, 10, "Installation:")
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New StartAllInstancesAction With {
                    .InstallationId = RuleEditorForm.GetSelectedId(installCombo)}, IAction),
                .LoadFn = Sub(a)
                              Dim sa = TryCast(a, StartAllInstancesAction)
                              If sa IsNot Nothing Then RuleEditorForm.SelectComboById(installCombo, sa.InstallationId)
                          End Sub}
        End Function

        Private Function BuildStopAllInstancesEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim installCombo = AddInstallationComboRow(panel, 10, "Installation:")
            Dim gracefulNum = AddNumericRow(panel, 40, "Graceful timeout:", "ms", 0, 600000, 10000)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New StopAllInstancesAction With {
                    .InstallationId = RuleEditorForm.GetSelectedId(installCombo),
                    .GracefulTimeoutMs = CInt(gracefulNum.Value)}, IAction),
                .LoadFn = Sub(a)
                              Dim sa = TryCast(a, StopAllInstancesAction)
                              If sa Is Nothing Then Return
                              RuleEditorForm.SelectComboById(installCombo, sa.InstallationId)
                              gracefulNum.Value = RuleEditorForm.ClampToRange(sa.GracefulTimeoutMs, gracefulNum)
                          End Sub}
        End Function

        Private Function BuildUpdateInstallationEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim installCombo = AddInstallationComboRow(panel, 10, "Installation:")
            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 45), .MaximumSize = New Size(660, 0),
                .Text = "Stops all instances of this installation, runs the update via SteamCMD, then starts them again."}
            panel.Controls.Add(help)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New UpdateInstallationAction With {
                    .InstallationId = RuleEditorForm.GetSelectedId(installCombo)}, IAction),
                .LoadFn = Sub(a)
                              Dim ua = TryCast(a, UpdateInstallationAction)
                              If ua IsNot Nothing Then RuleEditorForm.SelectComboById(installCombo, ua.InstallationId)
                          End Sub}
        End Function

        Private Function BuildSendRconEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim instanceCombo = AddInstanceComboRow(panel, 10, "Instance:")
            Dim cmdTxt = AddTextBoxRow(panel, 40, "Command:")
            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 75), .MaximumSize = New Size(660, 0),
                .Text = "RCON must be configured on the target instance. The plugin determines the protocol (Source/Minecraft/REST). Response is recorded in the execution history."}
            panel.Controls.Add(help)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New SendRconCommandAction With {
                    .InstanceId = RuleEditorForm.GetSelectedId(instanceCombo),
                    .Command = cmdTxt.Text.Trim()}, IAction),
                .LoadFn = Sub(a)
                              Dim ra = TryCast(a, SendRconCommandAction)
                              If ra Is Nothing Then Return
                              RuleEditorForm.SelectComboById(instanceCombo, ra.InstanceId)
                              cmdTxt.Text = If(ra.Command, "")
                          End Sub}
        End Function

        Private Function BuildNotifyEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim contentFont As New Font("Segoe UI", 9)

            Dim destLbl As New Label() With {
                .Text = "Destination:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 13)}
            panel.Controls.Add(destLbl)

            Dim pluginCombo As New ComboBox() With {
                .Location = New Point(120, 10), .Size = New Size(300, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            For Each dest In _notificationDestinations.OrderBy(Function(d) d.DisplayName)
                pluginCombo.Items.Add(New IdItem(dest.DestinationId, dest.DisplayName))
            Next
            panel.Controls.Add(pluginCombo)

            If _notificationDestinations.Count = 0 Then
                Dim emptyLbl As New Label() With {
                    .AutoSize = True,
                    .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                    .ForeColor = Color.FromArgb(150, 100, 30),
                    .Location = New Point(425, 13),
                    .Text = "(none configured — see Tools ▸ Notifications)"}
                panel.Controls.Add(emptyLbl)
            End If

            Dim msgTxt = AddTextBoxRow(panel, 40, "Message:")

            Dim sevLbl As New Label() With {
                .Text = "Severity:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(10, 73)}
            panel.Controls.Add(sevLbl)
            Dim sevCombo As New ComboBox() With {
                .Location = New Point(120, 70), .Size = New Size(180, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            For Each sev In [Enum].GetValues(GetType(NotificationSeverity))
                sevCombo.Items.Add(New IdItem(sev.ToString(), sev.ToString()))
            Next
            sevCombo.SelectedIndex = 0
            panel.Controls.Add(sevCombo)

            Dim tokenHelp As New Label() With {
                .AutoSize = True,
                .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 100),
                .MaximumSize = New Size(660, 0),
                .Text = "Tokens: {RuleName}, {InstanceName}, {InstallationName}, {NodeName}, {GameId}, {Time}, {Date}"}
            panel.Controls.Add(tokenHelp)

            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() As IAction
                               Dim sevItem = TryCast(sevCombo.SelectedItem, IdItem)
                               Dim sevVal As NotificationSeverity = NotificationSeverity.Info
                               If sevItem IsNot Nothing Then
                                   [Enum].TryParse(sevItem.Id, sevVal)
                               End If
                               Return New NotifyAction With {
                                   .DestinationId = RuleEditorForm.GetSelectedId(pluginCombo),
                                   .Message = msgTxt.Text,
                                   .Severity = sevVal}
                           End Function,
                .LoadFn = Sub(a)
                              Dim na = TryCast(a, NotifyAction)
                              If na Is Nothing Then Return
                              RuleEditorForm.SelectComboById(pluginCombo, na.DestinationId)
                              msgTxt.Text = If(na.Message, "")
                              RuleEditorForm.SelectComboById(sevCombo, na.Severity.ToString())
                          End Sub}
        End Function

        Private Function BuildWaitEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim durNum = AddNumericRow(panel, 10, "Duration:", "ms",
                                        0, Integer.MaxValue, 5000)
            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 45), .MaximumSize = New Size(660, 0),
                .Text = "Pauses execution for the given duration. Useful inside a sequence to space announcement messages before a coordinated restart."}
            panel.Controls.Add(help)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New WaitAction With {
                    .DurationMs = CInt(durNum.Value)}, IAction),
                .LoadFn = Sub(a)
                              Dim wa = TryCast(a, WaitAction)
                              If wa IsNot Nothing Then
                                  durNum.Value = RuleEditorForm.ClampToRange(wa.DurationMs, durNum)
                              End If
                          End Sub}
        End Function

        Private Function BuildWaitForReadyEditor() As ActionSubEditor
            Dim panel As New Panel()
            Dim instanceCombo = AddInstanceComboRow(panel, 10, "Instance:")
            Dim timeoutNum = AddNumericRow(panel, 40, "Timeout:", "s (0=plugin default)",
                                            0, 3600, 0)
            Dim help As New Label() With {
                .AutoSize = True, .Font = New Font("Segoe UI", 8.5F, FontStyle.Italic),
                .ForeColor = Color.FromArgb(100, 100, 100),
                .Location = New Point(10, 75), .MaximumSize = New Size(660, 0),
                .Text = "Waits for the plugin's declared ready signal. Returns success on signal, on timeout, or on terminal state — sequences shouldn't hang here indefinitely."}
            panel.Controls.Add(help)
            Return New ActionSubEditor With {
                .Panel = panel,
                .BuildFn = Function() CType(New WaitForReadySignalAction With {
                    .InstanceId = RuleEditorForm.GetSelectedId(instanceCombo),
                    .TimeoutSeconds = CInt(timeoutNum.Value)}, IAction),
                .LoadFn = Sub(a)
                              Dim wa = TryCast(a, WaitForReadySignalAction)
                              If wa Is Nothing Then Return
                              RuleEditorForm.SelectComboById(instanceCombo, wa.InstanceId)
                              timeoutNum.Value = RuleEditorForm.ClampToRange(wa.TimeoutSeconds, timeoutNum)
                          End Sub}
        End Function

    End Class

End Namespace
