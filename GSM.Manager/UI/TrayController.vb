Imports System
Imports System.Drawing
Imports System.Windows.Forms

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 5m-1 — owns the Manager's system-tray NotifyIcon.
    '''
    ''' 5m-1a scope: a static tray icon with a right-click context
    ''' menu (Open / Exit) and double-click-to-open. The icon lives
    ''' for the Manager's lifetime; MainForm constructs one in its
    ''' own constructor and disposes it on FormClosed.
    '''
    ''' This class is deliberately behaviour-free: what "Open" and
    ''' "Exit" actually do (restore the window; perform a real
    ''' application exit that bypasses any close-to-tray intercept)
    ''' lives in MainForm and is supplied as callbacks. The
    ''' minimize-/close-to-tray intercepts (5m-1b) are MainForm
    ''' window events, not tray concerns, so they live there too.
    ''' 5m-2b adds the optional "Restart in Safe Mode / Restart
    ''' Normally" entry (label + action supplied by MainForm).
    ''' </summary>
    Public Class TrayController
        Implements IDisposable

        Private ReadOnly _notifyIcon As NotifyIcon
        Private ReadOnly _onOpen As Action
        Private ReadOnly _onExit As Action
        Private _disposed As Boolean

        ''' <summary>
        ''' Build and show the tray icon.
        ''' </summary>
        ''' <param name="trayIcon">Icon to display; callers pass the
        ''' MainForm's icon (with a system fallback) so the tray
        ''' matches the app.</param>
        ''' <param name="tooltip">Hover tooltip text.</param>
        ''' <param name="onOpen">Invoked for the Open menu item and
        ''' for double-click.</param>
        ''' <param name="onExit">Invoked for the Exit menu item —
        ''' MainForm performs a real exit here.</param>
        ''' <param name="restartLabel">Optional label for a restart
        ''' entry between Open and Exit (5m-2b). When supplied with
        ''' onRestart, the entry is added; otherwise it's omitted.</param>
        ''' <param name="onRestart">Invoked for the restart entry.</param>
        Public Sub New(trayIcon As Icon, tooltip As String,
                       onOpen As Action, onExit As Action,
                       Optional restartLabel As String = Nothing,
                       Optional onRestart As Action = Nothing)
            _onOpen = onOpen
            _onExit = onExit

            Dim menu As New ContextMenuStrip()
            Dim openItem = menu.Items.Add("Open")
            AddHandler openItem.Click, Sub(sender, e) _onOpen?.Invoke()
            ' Phase 5m-2b — optional restart-into-mode entry.
            If Not String.IsNullOrEmpty(restartLabel) AndAlso onRestart IsNot Nothing Then
                menu.Items.Add(New ToolStripSeparator())
                Dim restartItem = menu.Items.Add(restartLabel)
                AddHandler restartItem.Click, Sub(sender, e) onRestart.Invoke()
            End If
            menu.Items.Add(New ToolStripSeparator())
            Dim exitItem = menu.Items.Add("Exit")
            AddHandler exitItem.Click, Sub(sender, e) _onExit?.Invoke()

            _notifyIcon = New NotifyIcon() With {
                .Text = If(String.IsNullOrEmpty(tooltip), "PowerGSM Manager", tooltip),
                .Icon = trayIcon,
                .ContextMenuStrip = menu,
                .Visible = True
            }
            ' Double-click restores the window. Single left-click is
            ' left alone; right-click surfaces the context menu —
            ' standard Windows tray convention.
            AddHandler _notifyIcon.DoubleClick, Sub(sender, e) _onOpen?.Invoke()
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            If _notifyIcon IsNot Nothing Then
                ' Hide before disposing — a NotifyIcon left Visible
                ' at dispose can linger in the tray until the user
                ' next hovers over the area (a long-standing WinForms
                ' quirk).
                _notifyIcon.Visible = False
                _notifyIcon.Dispose()
            End If
        End Sub

    End Class

End Namespace
