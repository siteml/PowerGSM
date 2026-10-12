Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api

' ============================================================
'  SecureNodesForm — Nodes → Secure All Nodes (HTTPS)
'  (docs/plans/HTTPS_SinglePort_Plan.md, slice 4 fleet action)
'
'  One up-front checklist of every attached node that is NOT yet
'  fully secured (HTTPS pinned + plain HTTP off), then a sequential
'  run of TlsOnboardingService.SecureNodeAsync per ticked node with a
'  live result column. Sequential by design: each node bounces twice
'  (enable, HTTP off) and the operator should see each one come back
'  before the next starts. A failure on one node never blocks the
'  others — the row shows the step that failed and the run moves on.
'
'  Columns: Secure (checkbox) | Node | Address | HTTPS now | Running |
'  Result. "Running" is the node's RunningInstanceCount from
'  /api/status — advisory: shim-supervised instances survive the
'  bounces, a pre-shim direct instance loses console capture until its
'  next restart. The plan's "skip nodes with running non-shim
'  instances unless overridden" is implemented as: rows with running
'  instances start UNTICKED and show the count, so overriding is one
'  click rather than a per-node prompt mid-run.
'
'  Token rotation (plan: "Rotate token" column) is NOT implemented —
'  the node has no rotate-token endpoint yet. The summary dialog
'  reminds the operator to rotate via NodeSetup + Edit Node. When the
'  endpoint exists, add the column here and call it right after each
'  node's HttpMode=Disabled succeeds (the plan's ordering).
'
'  VB rules: no Await in Catch/Finally; no async lambdas — the run is
'  a named Private Async Function.
' ============================================================

Namespace GSM.Manager.UI

    Public Class SecureNodesForm
        Inherits Form

        Private Class Row
            Public Property NodeId As String
            Public Property Display As String
            Public Property Host As String
            Public Property Port As Integer
            Public Property AuthToken As String
            Public Property TlsPort As Integer
            Public Property Probe As TlsProbeResult
            Public Property HttpMode As String      ' when ActiveHere; Nothing otherwise
            Public Property Running As Integer = -1 ' -1 = unknown
            Public Property Item As ListViewItem
        End Class

        Private _grid As ListView
        Private _status As Label
        Private _runButton As Button
        Private _closeButton As Button
        Private _refreshButton As Button
        ' Rotate each node's auth token as the last step (slice 4 "Rotate
        ' token"). Default ON. When ticked, already-secured nodes are listed
        ' too so a rotation-only pass over the fleet is one click.
        Private _rotateCheck As CheckBox
        Private _busy As Boolean
        Private ReadOnly _rows As New List(Of Row)

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            Me.Text = "Secure All Nodes (HTTPS)"
            Me.Size = New Size(860, 460)
            Me.MinimumSize = New Size(700, 360)
            Me.StartPosition = FormStartPosition.CenterParent
            Me.FormBorderStyle = FormBorderStyle.Sizable
            Me.MaximizeBox = False
            Me.MinimizeBox = False

            Dim intro As New Label() With {
                .Dock = DockStyle.Top,
                .Height = 58,
                .Padding = New Padding(8, 6, 8, 0),
                .Text = "For each ticked node: enable HTTPS on its listen port → verify over the pinned " &
                        "certificate → turn plain HTTP OFF → rotate the auth token. Up to three restarts per " &
                        "node. Shim-supervised instances survive; a pre-shim (direct) instance loses console " &
                        "capture until its next restart. Nodes run one at a time; a failure on one does not " &
                        "stop the others. Other Managers attached to these nodes must re-enter the token."
            }

            _grid = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .CheckBoxes = True,
                .FullRowSelect = True,
                .GridLines = True,
                .HideSelection = False
            }
            _grid.Columns.Add("Secure", 60)
            _grid.Columns.Add("Node", 170)
            _grid.Columns.Add("Address", 150)
            _grid.Columns.Add("HTTPS now", 160)
            _grid.Columns.Add("Running", 60)
            _grid.Columns.Add("Result", 260)

            Dim bottom As New Panel() With {.Dock = DockStyle.Bottom, .Height = 62}
            _status = New Label() With {
                .AutoSize = False, .Left = 8, .Top = 12, .Width = 440, .Height = 20,
                .ForeColor = SystemColors.GrayText
            }
            _rotateCheck = New CheckBox() With {
                .Text = "Rotate auth &tokens", .AutoSize = True, .Left = 8, .Top = 36, .Checked = True
            }
            AddHandler _rotateCheck.CheckedChanged, AddressOf OnReprobe
            _refreshButton = New Button() With {.Text = "Re-&probe", .Width = 90, .Height = 28, .Top = 8}
            AddHandler _refreshButton.Click, AddressOf OnReprobe
            _runButton = New Button() With {.Text = "&Secure ticked nodes", .Width = 150, .Height = 28, .Top = 8}
            AddHandler _runButton.Click, AddressOf OnRun
            _closeButton = New Button() With {.Text = "&Close", .Width = 80, .Height = 28, .Top = 8,
                                              .DialogResult = DialogResult.Cancel}
            bottom.Controls.Add(_status)
            bottom.Controls.Add(_rotateCheck)
            bottom.Controls.Add(_refreshButton)
            bottom.Controls.Add(_runButton)
            bottom.Controls.Add(_closeButton)
            AddHandler bottom.Resize, Sub()
                                          _closeButton.Left = bottom.Width - _closeButton.Width - 8
                                          _runButton.Left = _closeButton.Left - _runButton.Width - 8
                                          _refreshButton.Left = _runButton.Left - _refreshButton.Width - 8
                                      End Sub

            Me.Controls.Add(_grid)
            Me.Controls.Add(intro)
            Me.Controls.Add(bottom)
            Me.CancelButton = _closeButton

            AddHandler Me.Shown, AddressOf OnShownProbe
        End Sub

        ' --------------------------------------------------------
        '  Probe pass — one row per attached node
        ' --------------------------------------------------------

        Private Async Sub OnShownProbe(sender As Object, e As EventArgs)
            RemoveHandler Me.Shown, AddressOf OnShownProbe
            Await ProbeAllAsync()
        End Sub

        Private Async Sub OnReprobe(sender As Object, e As EventArgs)
            If _busy Then Return
            Await ProbeAllAsync()
        End Sub

        Private Async Function ProbeAllAsync() As Task
            _busy = True
            _runButton.Enabled = False
            _refreshButton.Enabled = False
            _status.Text = "Probing nodes..."
            _grid.Items.Clear()
            _rows.Clear()

            Dim onboarding = ManagerProgram.Services.GetService(Of TlsOnboardingService)()
            Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
            If onboarding Is Nothing OrElse factory Is Nothing Then
                _status.Text = "TLS onboarding service unavailable."
                _busy = False
                _refreshButton.Enabled = True
                Return
            End If

            ' Snapshot the node list on the UI thread.
            Dim nodes As New List(Of Row)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                For Each n In db.Nodes.Where(Function(x) x.IsEnabled).ToList().
                        OrderBy(Function(x) x.DisplayName, StringComparer.OrdinalIgnoreCase)
                    nodes.Add(New Row With {
                        .NodeId = n.NodeId,
                        .Display = If(n.DisplayName, n.NodeId),
                        .Host = n.HostAddress,
                        .Port = n.Port,
                        .AuthToken = n.AuthToken,
                        .TlsPort = n.TlsPort
                    })
                Next
            End Using

            For Each r In nodes
                Await ProbeRowAsync(onboarding, factory, r)
                ' Only list nodes that still have something to do: fully
                ' secured = Active + HTTP off + on the shared port. With
                ' rotation ticked everything Active is listed (rotation is
                ' not observable, so the operator decides).
                Dim onAltPort = r.TlsPort > 0 AndAlso r.TlsPort <> r.Port
                If r.Probe = TlsProbeResult.ActiveHere AndAlso Not onAltPort AndAlso
                   String.Equals(r.HttpMode, "Disabled", StringComparison.OrdinalIgnoreCase) AndAlso
                   Not _rotateCheck.Checked Then
                    Continue For
                End If
                AddRow(r)
            Next

            _status.Text = If(_rows.Count = 0,
                              "Every attached node is already secured (HTTPS pinned, HTTP off).",
                              $"{_rows.Count} node(s) listed — tick the ones to secure" &
                              If(_rotateCheck.Checked, " / rotate.", "."))
            _runButton.Enabled = _rows.Count > 0
            _refreshButton.Enabled = True
            _busy = False
        End Function

        Private Async Function ProbeRowAsync(onboarding As TlsOnboardingService,
                                             factory As NodeHttpClientFactory,
                                             r As Row) As Task
            Try
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    r.Probe = Await onboarding.ProbeAsync(r.NodeId, cts.Token)
                    If r.Probe = TlsProbeResult.ActiveHere Then
                        r.HttpMode = Await onboarding.GetHttpModeAsync(r.NodeId, cts.Token)
                    End If
                    If r.Probe <> TlsProbeResult.NodeUnreachable Then
                        Dim client = factory.GetClient(r.NodeId, r.Host, r.Port, r.AuthToken)
                        Dim st = Await client.GetStatusAsync(cts.Token)
                        If st IsNot Nothing Then r.Running = st.RunningInstanceCount
                    End If
                End Using
            Catch
                ' Row shows whatever we learned; unreachable/unknown is a
                ' valid display state.
            End Try
        End Function

        Private Sub AddRow(r As Row)
            Dim item As New ListViewItem("")
            item.SubItems.Add(r.Display)
            item.SubItems.Add($"{r.Host}:{r.Port}")
            item.SubItems.Add(DescribeProbe(r))
            item.SubItems.Add(If(r.Running < 0, "?", r.Running.ToString()))
            item.SubItems.Add("")
            item.Tag = r
            r.Item = item

            ' Default tick: eligible AND nothing running. Running nodes
            ' start unticked (the plan's "skip unless overridden").
            Dim eligible = r.Probe = TlsProbeResult.SupportedNotEnabled OrElse
                           r.Probe = TlsProbeResult.EnabledNotAdopted OrElse
                           r.Probe = TlsProbeResult.ActiveHere
            item.Checked = eligible AndAlso r.Running = 0
            If Not eligible Then item.ForeColor = SystemColors.GrayText

            _grid.Items.Add(item)
            _rows.Add(r)
        End Sub

        Private Shared Function DescribeProbe(r As Row) As String
            Select Case r.Probe
                Case TlsProbeResult.NodeUnreachable : Return "unreachable"
                Case TlsProbeResult.NotSupported : Return "not supported (update node)"
                Case TlsProbeResult.SupportedNotEnabled : Return "off"
                Case TlsProbeResult.EnabledNotAdopted : Return "on (not trusted here)"
                Case TlsProbeResult.ActiveHere
                    Dim portNote = If(r.TlsPort > 0 AndAlso r.TlsPort <> r.Port, $" (alt port {r.TlsPort})", "")
                    Return "active" & portNote & " — HTTP " & If(String.IsNullOrEmpty(r.HttpMode), "unknown", r.HttpMode)
                Case Else : Return "?"
            End Select
        End Function

        ' --------------------------------------------------------
        '  Run — sequential, per-row result
        ' --------------------------------------------------------

        Private Async Sub OnRun(sender As Object, e As EventArgs)
            If _busy Then Return
            Dim ticked = _rows.Where(Function(r) r.Item IsNot Nothing AndAlso r.Item.Checked).ToList()
            If ticked.Count = 0 Then
                _status.Text = "Nothing ticked."
                Return
            End If

            ' .Where().Count() rather than .Count(pred): on a List(Of T) VB
            ' binds the Count PROPERTY first and then tries to index it.
            Dim withRunning As Integer = ticked.Where(Function(r) r.Running > 0).Count()
            Dim rotate As Boolean = _rotateCheck.Checked
            Dim msg = $"Secure {ticked.Count} node(s) now?" & Environment.NewLine & Environment.NewLine &
                      If(rotate,
                         "Each node restarts up to three times (enable HTTPS, turn HTTP off, rotate token). " &
                         "Any OTHER Manager attached to these nodes is locked out until its token is re-entered.",
                         "Each node restarts up to twice (enable HTTPS, turn HTTP off). Any other Manager " &
                         "still using HTTP for these nodes will lose access.")
            If withRunning > 0 Then
                msg &= Environment.NewLine & Environment.NewLine &
                       $"{withRunning} of them have running instances. Shim-supervised instances survive; " &
                       "pre-shim direct instances lose console capture until their next restart."
            End If
            If MessageBox.Show(msg, "Secure nodes", MessageBoxButtons.YesNo,
                               MessageBoxIcon.Question) <> DialogResult.Yes Then
                Return
            End If

            Await RunAsync(ticked, rotate)
        End Sub

        Private Async Function RunAsync(ticked As List(Of Row), rotate As Boolean) As Task
            _busy = True
            _runButton.Enabled = False
            _refreshButton.Enabled = False
            _closeButton.Enabled = False
            Dim onboarding = ManagerProgram.Services.GetService(Of TlsOnboardingService)()

            Dim ok = 0
            Dim failed = 0
            For i = 0 To ticked.Count - 1
                Dim r = ticked(i)
                _status.Text = $"Securing {r.Display} ({i + 1} of {ticked.Count})..."
                SetResult(r, "working...", SystemColors.ControlText)

                Dim err As String = Nothing
                Try
                    err = Await onboarding.SecureNodeAsync(
                        r.NodeId,
                        Sub(line) SetResult(r, line, Color.DarkOrange),
                        CancellationToken.None,
                        rotateToken:=rotate)
                Catch ex As Exception
                    err = ex.Message
                End Try

                If err Is Nothing Then
                    ok += 1
                    SetResult(r, If(rotate, "secured — HTTPS pinned, HTTP off, token rotated",
                                           "secured — HTTPS pinned, HTTP off"), Color.DarkGreen)
                    r.Item.Checked = False
                Else
                    failed += 1
                    SetResult(r, err, Color.Firebrick)
                End If
            Next

            _status.Text = $"Done: {ok} secured, {failed} failed."
            _closeButton.Enabled = True
            _refreshButton.Enabled = True
            _runButton.Enabled = True
            _busy = False

            If ok > 0 AndAlso Not rotate Then
                MessageBox.Show(
                    $"{ok} node(s) secured without token rotation. Their tokens travelled in cleartext " &
                    "before HTTPS — run this again with ""Rotate auth tokens"" ticked when convenient.",
                    "Secure nodes", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        End Function

        ''' <summary>UI-thread-safe result-cell update; progress callbacks
        ''' from SecureNodeAsync may land on a pool thread.</summary>
        Private Sub SetResult(r As Row, text As String, color As Color)
            Try
                If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
                If Me.InvokeRequired Then
                    Me.BeginInvoke(Sub() SetResult(r, text, color))
                    Return
                End If
                If r.Item Is Nothing Then Return
                r.Item.SubItems(5).Text = text
                r.Item.SubItems(5).ForeColor = color
                r.Item.UseItemStyleForSubItems = False
            Catch
            End Try
        End Sub

    End Class

End Namespace
