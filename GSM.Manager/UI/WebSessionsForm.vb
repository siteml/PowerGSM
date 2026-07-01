Imports System
Imports System.Drawing
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager.Core
Imports GSM.Utility

Namespace GSM.Manager.UI

    ''' <summary>
    ''' Phase 7-5b — Manage Plugins → Web Sessions tab. A viewer and
    ''' revoke surface for the shared web-session store: lists each
    ''' stored session by key, the plugin that captured it, and when
    ''' (captured / last used). The cookie header itself is never
    ''' shown or retrievable here. "Revoke" calls
    ''' WebSessionStore.Invalidate, which clears the session from
    ''' cache + DB and lifts its prompt-block so the next request can
    ''' re-capture. "Validate" routes to the owning plugin's optional
    ''' IWebSessionValidator. Capture/login is deliberately NOT
    ''' offered here — only the owning plugin knows a session's start
    ''' URL and completion pattern, so sign-in stays a plugin action.
    '''
    ''' LAYOUT: docked (not absolute-positioned). This form is hosted
    ''' borderless inside ManagePluginsForm's TabControl, so its
    ''' client height is the tab page's, not this form's nominal Size.
    ''' Absolute bottom-anchored controls fell off the bottom of the
    ''' page; Dock-based flow adapts to whatever height the tab gives.
    ''' </summary>
    Public Class WebSessionsForm
        Inherits Form

        Private ReadOnly _store As WebSessionStore
        Private ReadOnly _host As UtilityPluginHost

        Private _listView As ListView
        Private _revokeButton As Button
        Private _validateButton As Button
        Private _refreshButton As Button
        Private _addAccountButton As Button
        Private _statusLabel As Label

        Public Sub New()
            _store = ManagerProgram.Services.GetService(Of WebSessionStore)()
            _host = ManagerProgram.Services.GetService(Of UtilityPluginHost)()
            FormIconHelper.ApplyTo(Me)
            InitializeControls()
            AddHandler Me.Load, Sub(s, e) RefreshList()
        End Sub

        Private Sub InitializeControls()
            Me.Text = "Web Sessions"
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MinimizeBox = False
            Me.StartPosition = FormStartPosition.CenterParent
            Me.ClientSize = New Size(760, 520)
            Me.MinimumSize = New Size(620, 380)

            ' Controls are added to a single Fill panel and docked so
            ' the layout survives being reparented (borderless) into a
            ' TabPage of a different height. Add order matters for Dock:
            ' Fill first, then Bottom strips, then Top — WinForms docks
            ' last-added closest to the edge, so add bottom/top AFTER
            ' fill, and the most-inset (status strip) BEFORE the button
            ' strip below it... actually we add in reverse-inset order.

            ' --- bottom button strip (docked bottom; can't fall off) ---
            Dim buttonStrip As New Panel With {.Dock = DockStyle.Bottom, .Height = 44}

            Dim closeButton As New Button With {
                .Text = "Close", .Size = New Size(90, 28), .DialogResult = DialogResult.OK,
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right,
                .Location = New Point(buttonStrip.Width - 102, 8)}
            buttonStrip.Controls.Add(closeButton)
            Me.CancelButton = closeButton
            Me.AcceptButton = closeButton

            _revokeButton = New Button With {
                .Text = "Revoke", .Size = New Size(90, 28), .Enabled = False,
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right,
                .Location = New Point(buttonStrip.Width - 198, 8)}
            AddHandler _revokeButton.Click, Sub(s, e) OnRevoke()
            buttonStrip.Controls.Add(_revokeButton)

            _validateButton = New Button With {
                .Text = "Validate", .Size = New Size(90, 28), .Enabled = False,
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right,
                .Location = New Point(buttonStrip.Width - 294, 8)}
            AddHandler _validateButton.Click, Sub(s, e) OnValidate()
            buttonStrip.Controls.Add(_validateButton)

            _refreshButton = New Button With {
                .Text = "Refresh", .Size = New Size(90, 28),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Left,
                .Location = New Point(12, 8)}
            AddHandler _refreshButton.Click, Sub(s, e) RefreshList()
            buttonStrip.Controls.Add(_refreshButton)

            ' "Add account…" forces a fresh portal login (even when
            ' sessions already exist) so the operator can hold several
            ' myrealm accounts. Routes to the single portal provider via
            ' the host; disabled when no portal provider is loaded.
            _addAccountButton = New Button With {
                .Text = "Add account…", .Size = New Size(110, 28),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Left,
                .Location = New Point(108, 8),
                .Enabled = (_host IsNot Nothing AndAlso _host.HasAnyPortalProvider())}
            AddHandler _addAccountButton.Click, Sub(s, e) OnAddAccount()
            buttonStrip.Controls.Add(_addAccountButton)

            ' --- status strip (docked bottom, above the buttons) ---
            Dim statusStrip As New Panel With {.Dock = DockStyle.Bottom, .Height = 24}
            _statusLabel = New Label With {
                .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft,
                .Padding = New Padding(12, 0, 12, 0),
                .AutoEllipsis = True, .ForeColor = SystemColors.GrayText, .Text = ""}
            statusStrip.Controls.Add(_statusLabel)

            ' --- header (docked top) ---
            Dim header As New Label With {
                .Dock = DockStyle.Top, .Height = 44,
                .Padding = New Padding(12, 10, 12, 0),
                .Text = "Login sessions held for plugins (e.g. portal logins). " &
                        "Cookie contents are never shown. Revoking clears a session; " &
                        "the owning plugin re-captures it on next need.",
                .ForeColor = SystemColors.GrayText}

            ' --- list (fills the remainder) ---
            _listView = New ListView With {
                .Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True,
                .GridLines = True, .MultiSelect = False, .HideSelection = False}
            _listView.Columns.Add("Session", 185)
            _listView.Columns.Add("Captured by", 140)
            _listView.Columns.Add("Captured", 135)
            _listView.Columns.Add("Last used", 135)
            AddHandler _listView.SelectedIndexChanged, Sub(s, e) UpdateButtons()

            ' Add fill FIRST, then docked edges, so the fill takes the
            ' leftover centre and the strips stack at top/bottom.
            Me.Controls.Add(_listView)
            Me.Controls.Add(statusStrip)
            Me.Controls.Add(buttonStrip)
            Me.Controls.Add(header)
        End Sub

        Private Sub RefreshList()
            _listView.Items.Clear()
            If _store Is Nothing Then
                _statusLabel.Text = "Web-session store unavailable."
                UpdateButtons()
                Return
            End If

            Dim sessions = _store.ListSessions()
            For Each s In sessions
                Dim item As New ListViewItem(s.SessionKey)
                item.SubItems.Add(If(String.IsNullOrEmpty(s.CapturedByPluginId), "—", s.CapturedByPluginId))
                item.SubItems.Add(FormatLocal(s.CapturedAtUtc))
                item.SubItems.Add(If(s.LastUsedUtc.HasValue, FormatLocal(s.LastUsedUtc.Value), "—"))
                item.Tag = s
                _listView.Items.Add(item)
            Next

            _statusLabel.Text = If(sessions.Count = 0, "No stored web sessions.",
                                   If(sessions.Count = 1, "1 stored web session.",
                                      $"{sessions.Count} stored web sessions."))
            _addAccountButton.Enabled = (_host IsNot Nothing AndAlso _host.HasAnyPortalProvider())
            UpdateButtons()
        End Sub

        ''' <summary>UTC stored values rendered in local time for the
        ''' user. Unspecified-kind reads from SQLite are treated as
        ''' UTC before conversion.</summary>
        Private Shared Function FormatLocal(utc As DateTime) As String
            Dim asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            Return asUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        End Function

        Private Function SelectedSession() As WebSessionInfo
            If _listView.SelectedItems.Count = 0 Then Return Nothing
            Return TryCast(_listView.SelectedItems(0).Tag, WebSessionInfo)
        End Function

        Private Sub UpdateButtons()
            Dim s = SelectedSession()
            _revokeButton.Enabled = s IsNot Nothing
            ' Validate needs a plugin that claims the key AND opts into
            ' IWebSessionValidator — keys without one stay disabled.
            _validateButton.Enabled = s IsNot Nothing AndAlso
                                      _host IsNot Nothing AndAlso
                                      _host.HasValidatorFor(s.SessionKey)
        End Sub

        ''' <summary>Async Sub (not a lambda) so awaits resume on the
        ''' UI thread and controls can be touched directly after.</summary>
        Private Async Sub OnValidate()
            Dim s = SelectedSession()
            If s Is Nothing OrElse _host Is Nothing Then Return

            _validateButton.Enabled = False
            _statusLabel.Text = $"Validating ""{s.SessionKey}""…"
            Dim result As WebSessionValidationResult
            Try
                result = Await _host.ValidateSessionAsync(s.SessionKey)
            Catch ex As Exception
                result = New WebSessionValidationResult With {
                    .State = WebSessionValidationState.Failed, .Detail = ex.Message}
            End Try
            UpdateButtons()

            Dim detail = If(String.IsNullOrEmpty(result.Detail), "", $" — {result.Detail}")
            Select Case result.State
                Case WebSessionValidationState.Valid
                    _statusLabel.Text = $"""{s.SessionKey}"" is valid{detail}"
                Case WebSessionValidationState.Expired
                    _statusLabel.Text = $"""{s.SessionKey}"" has expired{detail}"
                    If MessageBox.Show(Me,
                            $"The session ""{s.SessionKey}"" no longer authenticates." & Environment.NewLine &
                            Environment.NewLine & "Revoke it now so the plugin can prompt for a fresh login?",
                            "Session Expired", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) = DialogResult.Yes Then
                        _store.Invalidate("(manual)", s.SessionKey)
                        RefreshList()
                    End If
                Case Else
                    _statusLabel.Text = $"Couldn't validate ""{s.SessionKey}""{detail}"
            End Select
        End Sub

        Private Sub OnRevoke()
            Dim s = SelectedSession()
            If s Is Nothing OrElse _store Is Nothing Then Return

            If MessageBox.Show(Me,
                    $"Revoke the stored session ""{s.SessionKey}""?" & Environment.NewLine & Environment.NewLine &
                    "The plugin that uses it will need to sign in again the next time it needs the session.",
                    "Revoke Web Session", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> DialogResult.Yes Then Return

            _store.Invalidate("(manual)", s.SessionKey)
            _statusLabel.Text = $"Revoked ""{s.SessionKey}""."
            RefreshList()
        End Sub

        ''' <summary>Async Sub (not a lambda) so awaits resume on the UI
        ''' thread. Forces a fresh portal login via the host, then
        ''' refreshes and selects the new/updated row. A cancelled or
        ''' failed sign-in returns Nothing — reported, not error-boxed,
        ''' since cancel is normal.</summary>
        Private Async Sub OnAddAccount()
            If _host Is Nothing Then Return
            _addAccountButton.Enabled = False
            _statusLabel.Text = "Opening the portal login…"

            Dim label As String = Nothing
            Try
                label = Await _host.AddPortalAccountAsync()
            Catch ex As Exception
                _statusLabel.Text = $"Add account failed — {ex.Message}"
                _addAccountButton.Enabled = (_host IsNot Nothing AndAlso _host.HasAnyPortalProvider())
                Return
            End Try

            _addAccountButton.Enabled = (_host IsNot Nothing AndAlso _host.HasAnyPortalProvider())
            If String.IsNullOrEmpty(label) Then
                _statusLabel.Text = "No account added (sign-in cancelled or failed)."
                Return
            End If

            RefreshList()
            SelectSessionByLabel(label)
            _statusLabel.Text = $"Added account ""{label}""."
        End Sub

        ''' <summary>Selects the row whose session key is, or ends with
        ''' ":", the given account label — so the just-added account is
        ''' highlighted after refresh. Generic: no hard-coded prefix.</summary>
        Private Sub SelectSessionByLabel(label As String)
            If String.IsNullOrEmpty(label) Then Return
            For Each item As ListViewItem In _listView.Items
                Dim info = TryCast(item.Tag, WebSessionInfo)
                If info Is Nothing Then Continue For
                Dim key = If(info.SessionKey, "")
                If String.Equals(key, label, StringComparison.OrdinalIgnoreCase) OrElse
                   key.EndsWith(":" & label, StringComparison.OrdinalIgnoreCase) Then
                    item.Selected = True
                    item.EnsureVisible()
                    Return
                End If
            Next
        End Sub

    End Class

End Namespace
