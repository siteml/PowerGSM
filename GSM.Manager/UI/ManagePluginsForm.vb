Imports System
Imports System.Drawing
Imports System.Windows.Forms

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 6-4 — Tools → Manage Plugins. Consolidates the three
    ''' plugin dialogs (Status, Sources, Updates) into one tabbed
    ''' window. Each tab HOSTS the existing form (TopLevel=False,
    ''' borderless, docked) rather than duplicating its logic, so the
    ''' three forms stay independently maintainable; this shell is
    ''' just chrome.
    ''' </summary>
    Public Class ManagePluginsForm
        Inherits Form

        Public Const TabStatus As Integer = 0
        Public Const TabSources As Integer = 1
        Public Const TabUpdates As Integer = 2
        Public Const TabWebSessions As Integer = 3

        Private _tabs As TabControl

        Public Sub New(Optional initialTab As Integer = TabStatus)
            FormIconHelper.ApplyTo(Me)
            InitializeControls(initialTab)
        End Sub

        Private Sub InitializeControls(initialTab As Integer)
            Me.Text = "Manage Plugins"
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.Size = New Size(820, 660)
            Me.MinimumSize = New Size(700, 520)

            _tabs = New TabControl With {.Dock = DockStyle.Fill}

            _tabs.TabPages.Add(MakeHostedTab("Status", New PluginStatusForm()))
            _tabs.TabPages.Add(MakeHostedTab("Sources", New PluginSourcesForm()))
            _tabs.TabPages.Add(MakeHostedTab("Updates", New PluginUpdatesForm()))
            _tabs.TabPages.Add(MakeHostedTab("Web Sessions", New WebSessionsForm()))

            If initialTab >= 0 AndAlso initialTab < _tabs.TabPages.Count Then
                _tabs.SelectedIndex = initialTab
            End If

            Me.Controls.Add(_tabs)
        End Sub

        ''' <summary>
        ''' Embed a Form into a TabPage. TopLevel=False lets a Form be
        ''' parented like a control; the hosted form's own Close (e.g.
        ''' its Close button, or Esc via its CancelButton) closes the
        ''' whole Manage Plugins window, which is the natural reading.
        ''' </summary>
        Private Function MakeHostedTab(title As String, hosted As Form) As TabPage
            Dim page As New TabPage(title)

            hosted.TopLevel = False
            hosted.FormBorderStyle = FormBorderStyle.None
            hosted.Dock = DockStyle.Fill
            ' Hosted forms keep their CancelButton/AcceptButton wiring;
            ' when one closes itself, close the shell too.
            AddHandler hosted.FormClosed, Sub(s, e) Me.Close()

            ' DialogResult buttons only auto-close forms shown via
            ' ShowDialog; hosted (modeless) forms' Close buttons would
            ' go dead. Rewire them to close the shell.
            WireDialogResultButtons(hosted)

            page.Controls.Add(hosted)
            hosted.Show()
            Return page
        End Function

        Private Sub WireDialogResultButtons(root As Control)
            For Each child As Control In root.Controls
                Dim btn = TryCast(child, Button)
                If btn IsNot Nothing AndAlso btn.DialogResult <> DialogResult.None Then
                    AddHandler btn.Click, Sub(s, e) Me.Close()
                ElseIf child.HasChildren Then
                    WireDialogResultButtons(child)
                End If
            Next
        End Sub

    End Class

End Namespace
