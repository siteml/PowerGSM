Imports System
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports GSM.Manager

' ============================================================
'  SafeModeFeaturesForm — Phase 5m-2c
'
'  Re-enable individual subsystems while STAYING in safe mode,
'  for iterative fix-and-test. The motivating case: a runaway
'  automation rule. Boot safe mode (engine off), fix the rule in
'  the rule editor, then turn just the automation engine back on
'  here to verify — if it still loops you're still in the safe
'  harbour and can re-fix, instead of a full Restart Normally /
'  crash / bounce-back cycle.
'
'  Re-enable only: there's no disable here. To turn a subsystem
'  back off, restart safe mode (a clean, known-good baseline).
'  The actual start logic lives in ManagerProgram.StartSubsystem;
'  this form is just the surface.
' ============================================================

Namespace GSM.Manager.UI

    Public Class SafeModeFeaturesForm
        Inherits Form

        Private _list As ListView
        Private _enableButton As Button
        Private _enableAllButton As Button

        Private ReadOnly _subsystems As ManagerSubsystem() = {
            ManagerSubsystem.Plugins,
            ManagerSubsystem.NodePolling,
            ManagerSubsystem.Notifications,
            ManagerSubsystem.Automation,
            ManagerSubsystem.VersionCheck,
            ManagerSubsystem.ChatPruner
        }

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            RefreshList()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Safe Mode — Re-enable Features"
            Me.Size = New Size(560, 420)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.MinimizeBox = False
            Me.MaximizeBox = False

            Dim intro As New Label()
            intro.AutoSize = False
            intro.Text = "Safe mode started these subsystems disabled. Re-enable them one at a " &
                         "time to fix and test — for example, fix a bad automation rule, then " &
                         "enable just the automation engine to verify it. To turn something back " &
                         "off, restart safe mode."
            intro.Location = New Point(20, 15)
            intro.Size = New Size(510, 60)
            Me.Controls.Add(intro)

            _list = New ListView()
            _list.View = View.Details
            _list.FullRowSelect = True
            _list.GridLines = True
            _list.MultiSelect = False
            _list.HideSelection = False
            _list.Location = New Point(20, 85)
            _list.Size = New Size(510, 220)
            _list.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                           AnchorStyles.Right Or AnchorStyles.Bottom
            _list.Columns.Add("Feature", 360)
            _list.Columns.Add("Status", 130)
            AddHandler _list.SelectedIndexChanged, Sub(s, e) UpdateButtons()
            Me.Controls.Add(_list)

            _enableButton = New Button()
            _enableButton.Text = "Enable"
            _enableButton.Size = New Size(120, 32)
            _enableButton.Location = New Point(20, 320)
            _enableButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
            _enableButton.Enabled = False
            AddHandler _enableButton.Click, AddressOf OnEnable
            Me.Controls.Add(_enableButton)

            _enableAllButton = New Button()
            _enableAllButton.Text = "Enable All"
            _enableAllButton.Size = New Size(120, 32)
            _enableAllButton.Location = New Point(150, 320)
            _enableAllButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Left
            AddHandler _enableAllButton.Click, AddressOf OnEnableAll
            Me.Controls.Add(_enableAllButton)

            Dim closeButton As New Button()
            closeButton.Text = "Close"
            closeButton.Size = New Size(100, 32)
            closeButton.Location = New Point(430, 320)
            closeButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
            AddHandler closeButton.Click, Sub(s, e) Me.Close()
            Me.Controls.Add(closeButton)
            Me.CancelButton = closeButton
        End Sub

        Private Sub RefreshList()
            _list.Items.Clear()
            For Each feature In _subsystems
                Dim started = ManagerProgram.IsSubsystemStarted(feature)
                Dim item As New ListViewItem(ManagerProgram.SubsystemDisplayName(feature))
                item.SubItems.Add(If(started, "Enabled", "Disabled"))
                item.Tag = feature
                If started Then item.ForeColor = Color.Gray
                _list.Items.Add(item)
            Next
            UpdateButtons()
            _enableAllButton.Enabled =
                _subsystems.Any(Function(f) Not ManagerProgram.IsSubsystemStarted(f))
        End Sub

        Private Sub UpdateButtons()
            Dim canEnable = False
            If _list.SelectedItems.Count > 0 Then
                Dim feature = CType(_list.SelectedItems(0).Tag, ManagerSubsystem)
                canEnable = Not ManagerProgram.IsSubsystemStarted(feature)
            End If
            _enableButton.Enabled = canEnable
        End Sub

        Private Sub OnEnable(sender As Object, e As EventArgs)
            If _list.SelectedItems.Count = 0 Then Return
            Dim feature = CType(_list.SelectedItems(0).Tag, ManagerSubsystem)
            EnableOne(feature)
            RefreshList()
        End Sub

        Private Sub OnEnableAll(sender As Object, e As EventArgs)
            For Each feature In _subsystems
                EnableOne(feature)
            Next
            RefreshList()
        End Sub

        ''' <summary>
        ''' Enable one subsystem, surfacing any start error. The start
        ''' call can block briefly (e.g. Discord connect), so show a
        ''' wait cursor. Already-started subsystems short-circuit in
        ''' ManagerProgram.StartSubsystem, so Enable All is safe to run
        ''' over the whole set.
        ''' </summary>
        Private Sub EnableOne(feature As ManagerSubsystem)
            If ManagerProgram.IsSubsystemStarted(feature) Then Return

            Dim err As String = Nothing
            Me.Cursor = Cursors.WaitCursor
            Try
                err = ManagerProgram.StartSubsystem(feature)
            Finally
                Me.Cursor = Cursors.Default
            End Try

            If Not String.IsNullOrEmpty(err) Then
                MessageBox.Show(
                    $"Couldn't enable {ManagerProgram.SubsystemDisplayName(feature)}:" &
                    Environment.NewLine & Environment.NewLine & err,
                    "Enable Feature", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If
        End Sub

    End Class

End Namespace
