Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Windows.Forms
Imports GSM.Automation

' ============================================================
'  StepEditorForm — modal editor for one step of a SequenceAction
'
'  Phase 4b-3: small companion to RuleEditorForm. The parent
'  form's sequence sub-editor opens this modal to add or edit
'  one step. Returns the resulting IAction via ResultStep so
'  the parent can splice it into its step list.
'
'  Type combo excludes "sequence" — no nested-sequence UI. The
'  serializer supports nesting but the UX gets unwieldy fast,
'  and use cases for nested sequences are rare enough that the
'  power user can hand-edit the JSON if they really need it.
'
'  Otherwise mirrors the same structure as ConditionEditorForm:
'  type combo + sub-panel + Save/Cancel. Uses ActionEditorFactory
'  to build the per-type sub-editors so we don't duplicate the
'  11 builders.
' ============================================================

Namespace GSM.Manager.UI

    Public Class StepEditorForm
        Inherits Form

        Private ReadOnly _factory As ActionEditorFactory
        Private ReadOnly _existing As IAction

        ''' <summary>
        ''' The action the user authored. Read by the caller
        ''' after ShowDialog returns DialogResult.OK.
        ''' </summary>
        Public Property ResultStep As IAction

        Private _typeCombo As ComboBox
        Private _subPanel As Panel
        Private _currentEditor As ActionSubEditor

        Public Sub New(factory As ActionEditorFactory,
                       Optional existing As IAction = Nothing)
            FormIconHelper.ApplyTo(Me)
            If factory Is Nothing Then
                Throw New ArgumentNullException(NameOf(factory))
            End If
            _factory = factory
            _existing = existing
            InitializeControls()
            If _existing IsNot Nothing Then LoadExisting()
        End Sub

        Private Sub InitializeControls()
            Me.Text = If(_existing Is Nothing, "Add Step", "Edit Step")
            ' Form sizing math: Me.Size is OUTER size, so drawable
            ' (client) area = Size.Height minus title bar (~30) minus
            ' borders (~4) on FixedDialog = ~34px non-client. Buttons
            ' at y=225 + height 32 ended at 257 — with previous 290
            ' outer height that bottom edge clipped against the form
            ' border. 305 outer gives a ~14px margin below the buttons
            ' which looks visually balanced.
            Me.Size = New Size(740, 305)
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent

            Dim grp As New GroupBox() With {
                .Text = "Step",
                .Location = New Point(15, 10),
                .Size = New Size(700, 215),
                .Font = New Font("Segoe UI", 9, FontStyle.Bold)}
            Me.Controls.Add(grp)

            Dim contentFont As New Font("Segoe UI", 9)

            Dim typeLbl As New Label() With {
                .Text = "Type:", .AutoSize = True, .Font = contentFont,
                .Location = New Point(15, 28)}
            grp.Controls.Add(typeLbl)
            _typeCombo = New ComboBox() With {
                .Location = New Point(60, 25), .Size = New Size(310, 24),
                .DropDownStyle = ComboBoxStyle.DropDownList, .Font = contentFont}
            ' Same set as RuleEditorForm's action picker minus
            ' "sequence". Keep ordering consistent so muscle memory
            ' transfers between forms.
            _typeCombo.Items.AddRange(New Object() {
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
                New IdItem("wait_for_ready", "Wait For Ready Signal")})
            _typeCombo.SelectedIndex = 0
            AddHandler _typeCombo.SelectedIndexChanged, Sub(s, e) OnTypeChanged()
            grp.Controls.Add(_typeCombo)

            _subPanel = New Panel() With {
                .Location = New Point(15, 60),
                .Size = New Size(670, 150),
                .BackColor = SystemColors.Window,
                .BorderStyle = BorderStyle.FixedSingle}
            grp.Controls.Add(_subPanel)

            ' Buttons. y=230 sits ~10px below the GroupBox bottom
            ' (group at y=10 + height 215 = 225) and gives ~14px
            ' breathing room above the form bottom inside the
            ' 305-tall form (client area ~271).
            Dim saveBtn As New Button() With {
                .Text = "Save", .Size = New Size(100, 32),
                .Location = New Point(505, 230)}
            AddHandler saveBtn.Click, AddressOf OnSave
            Me.Controls.Add(saveBtn)
            Me.AcceptButton = saveBtn

            Dim cancelBtn As New Button() With {
                .Text = "Cancel", .Size = New Size(100, 32),
                .Location = New Point(615, 230),
                .DialogResult = DialogResult.Cancel}
            Me.Controls.Add(cancelBtn)
            Me.CancelButton = cancelBtn

            ' Mount default sub-editor.
            OnTypeChanged()
        End Sub

        Private Sub OnTypeChanged()
            _subPanel.Controls.Clear()
            Dim selectedItem = TryCast(_typeCombo.SelectedItem, IdItem)
            Dim id As String = If(selectedItem IsNot Nothing,
                                   selectedItem.Id, "coordinated_restart")
            _currentEditor = _factory.BuildEditor(id)
            If _currentEditor IsNot Nothing AndAlso _currentEditor.Panel IsNot Nothing Then
                _currentEditor.Panel.Dock = DockStyle.Fill
                _subPanel.Controls.Add(_currentEditor.Panel)
            End If
        End Sub

        Private Sub LoadExisting()
            If _existing Is Nothing Then Return
            ' Sequence-as-a-step is rejected here even though
            ' technically possible in the data model. Falling back
            ' to coordinated_restart preserves the user's other
            ' fields rather than crashing — though they'll need to
            ' rebuild the action.
            If TypeOf _existing Is SequenceAction Then
                MessageBox.Show(
                    "This step is a nested sequence, which can't be edited in the UI." & vbCrLf &
                    "Cancel and edit the rule's JSON directly to keep the nested sequence.",
                    "Nested Sequence",
                    MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Dim typeId = ActionEditorFactory.GetActionTypeId(_existing)
            RuleEditorForm.SelectComboById(_typeCombo, typeId)
            ' SelectComboById is a no-op when the index already
            ' matches the default (coordinated_restart at position
            ' 0). Force OnTypeChanged so the right sub-editor is
            ' mounted even in that case. Same WinForms gotcha as
            ' the other modals.
            OnTypeChanged()
            If _currentEditor IsNot Nothing AndAlso _currentEditor.LoadFn IsNot Nothing Then
                _currentEditor.LoadFn.Invoke(_existing)
            End If
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            If _currentEditor Is Nothing OrElse _currentEditor.BuildFn Is Nothing Then
                MessageBox.Show("No step editor active.", "Validation",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim built = _currentEditor.BuildFn.Invoke()
            Dim err = ActionEditorFactory.ValidateAction(built)
            If Not String.IsNullOrEmpty(err) Then
                MessageBox.Show(err, "Validation",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            ResultStep = built
            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

    End Class

End Namespace
