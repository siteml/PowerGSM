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
'  DiagnosticsWindow — Tools → Diagnostics (2026-08-14).
'
'  Fleet/console-pipeline troubleshooting without curl. Plan:
'  docs/plans/Diagnostics_Plan.md (this file is Wave 1).
'
'  Layout: tree on the left (Fleet → node → instances), detail
'  panel on the right whose content depends on the selection:
'
'    Fleet / node selected → PROBE GRID: one row per attached
'      node — reachable, latency, Build, Protocol, Contracts,
'      Platform, EventStream, TLS. "Probe All" re-runs
'      GetApiVersionAsync(force:=True) fleet-wide. This is the
'      coalesced multi-node view; mixed-version fleets (the
'      recurring caveat class) show up here at a glance.
'
'    Instance selected → tabs:
'      Event sources — GET /events/sources via the additive
'        INodeClient.GetEventSourceDiagnosticsAsync: appended/
'        suppressed counters + authority map (gate G3 as a
'        click). 404 (pre-stream node) renders as a status
'        line, never an error dialog.
'      Stream health — Manager-local: EventSyncService state
'        (Inactive/Pending/Active), legacy-ingestion
'        suppression, sync cursor seq.
'
'  Wave 2 (node endpoints required — watermarks, ParseTrace,
'  rule dry-run, boot/resurrection state, shim link state)
'  adds tabs to the same instance view; see the plan doc.
'
'  Threading: every probe/refresh is async; results marshal
'  back via the captured WinForms sync context (standard
'  Async Sub handlers). A generation counter guards against
'  a stale refresh landing after the selection changed.
' ============================================================

Namespace GSM.Manager.UI

    Public Class DiagnosticsWindow
        Inherits Form

        ' ---- Tree node payloads ----
        Private Class NodeRef
            Public NodeId As String
            Public DisplayName As String
            Public HostAddress As String
            Public Port As Integer
            Public AuthToken As String
            Public Attached As Boolean
        End Class

        Private Class InstanceRef
            Public InstanceId As String
            Public DisplayName As String
            Public Node As NodeRef
        End Class

        ' ---- Controls ----
        Private _tree As TreeView
        Private _detailPanel As Panel

        ' Probe view
        Private _probePanel As Panel
        Private _probeList As ListView
        Private _probeButton As Button
        Private _probeStatus As Label

        ' Instance view
        Private _instancePanel As Panel
        Private _instanceTabs As TabControl
        Private _sourcesList As ListView
        Private _sourcesRefresh As Button
        Private _sourcesStatus As Label
        Private _streamHealthList As ListView
        Private _streamRefresh As Button

        ' Wave 2 tabs
        Private _watermarksList As ListView
        Private _watermarksStatus As Label
        Private _traceList As ListView
        Private _traceStatus As Label
        Private _testLineBox As TextBox
        Private _testResultsList As ListView
        Private _testStatus As Label
        Private _bootList As ListView
        Private _bootStatus As Label
        Private _shimList As ListView
        Private _shimStatus As Label

        ''' <summary>Bumped on every tree selection change; async
        ''' refreshes capture it and drop their results if it moved
        ''' (stale response for a deselected instance).</summary>
        Private _selectionGeneration As Integer

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            Me.Text = "Diagnostics"
            Me.Size = New Size(1000, 620)
            Me.MinimumSize = New Size(760, 420)
            Me.StartPosition = FormStartPosition.CenterParent

            Dim split As New SplitContainer() With {
                .Dock = DockStyle.Fill,
                .SplitterDistance = 260,
                .FixedPanel = FixedPanel.Panel1
            }

            _tree = New TreeView() With {
                .Dock = DockStyle.Fill,
                .HideSelection = False
            }
            AddHandler _tree.AfterSelect, AddressOf OnTreeSelect
            split.Panel1.Controls.Add(_tree)

            _detailPanel = New Panel() With {.Dock = DockStyle.Fill}
            split.Panel2.Controls.Add(_detailPanel)

            BuildProbePanel()
            BuildInstancePanel()
            _detailPanel.Controls.Add(_probePanel)
            _detailPanel.Controls.Add(_instancePanel)
            _instancePanel.Visible = False

            Me.Controls.Add(split)

            LoadTree()
        End Sub

        ' --------------------------------------------------------
        '  Tree
        ' --------------------------------------------------------

        Private Sub LoadTree()
            _tree.BeginUpdate()
            _tree.Nodes.Clear()
            Dim root As New TreeNode("Fleet")
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim installsByNode = db.Installations.ToList().
                        GroupBy(Function(i) i.NodeId).
                        ToDictionary(Function(g) g.Key, Function(g) g.ToList())
                    Dim instancesByInstall = db.Instances.ToList().
                        GroupBy(Function(i) i.InstallationId).
                        ToDictionary(Function(g) g.Key, Function(g) g.ToList())

                    For Each n In db.Nodes.ToList().
                            OrderBy(Function(x) x.DisplayName, StringComparer.OrdinalIgnoreCase)
                        Dim nref As New NodeRef With {
                            .NodeId = n.NodeId,
                            .DisplayName = If(n.DisplayName, n.NodeId),
                            .HostAddress = n.HostAddress,
                            .Port = n.Port,
                            .AuthToken = n.AuthToken,
                            .Attached = n.IsEnabled
                        }
                        Dim label = nref.DisplayName & If(nref.Attached, "", " (detached)")
                        Dim nodeTreeNode As New TreeNode(label) With {.Tag = nref}

                        Dim installs As List(Of InstallationEntity) = Nothing
                        If installsByNode.TryGetValue(n.NodeId, installs) Then
                            For Each inst In installs
                                Dim instances As List(Of InstanceEntity) = Nothing
                                If Not instancesByInstall.TryGetValue(inst.InstallationId, instances) Then Continue For
                                For Each ie In instances
                                    Dim iref As New InstanceRef With {
                                        .InstanceId = ie.InstanceId,
                                        .DisplayName = $"{inst.DisplayName}/{ie.DisplayName}",
                                        .Node = nref
                                    }
                                    nodeTreeNode.Nodes.Add(New TreeNode(iref.DisplayName) With {.Tag = iref})
                                Next
                            Next
                        End If
                        root.Nodes.Add(nodeTreeNode)
                    Next
                End Using
            Catch ex As Exception
                root.Nodes.Add(New TreeNode($"(failed to load: {ex.Message})"))
            End Try
            _tree.Nodes.Add(root)
            root.Expand()
            _tree.EndUpdate()
            _tree.SelectedNode = root
        End Sub

        Private Sub OnTreeSelect(sender As Object, e As TreeViewEventArgs)
            _selectionGeneration += 1
            Dim iref = TryCast(e.Node?.Tag, InstanceRef)
            If iref IsNot Nothing Then
                _probePanel.Visible = False
                _instancePanel.Visible = True
                RefreshInstanceView(iref)
            Else
                _instancePanel.Visible = False
                _probePanel.Visible = True
            End If
        End Sub

        ''' <summary>All NodeRefs currently in the tree.</summary>
        Private Function AllNodeRefs() As List(Of NodeRef)
            Dim result As New List(Of NodeRef)
            If _tree.Nodes.Count = 0 Then Return result
            For Each tn As TreeNode In _tree.Nodes(0).Nodes
                Dim nref = TryCast(tn.Tag, NodeRef)
                If nref IsNot Nothing Then result.Add(nref)
            Next
            Return result
        End Function

        Private Function SelectedInstanceRef() As InstanceRef
            Return TryCast(_tree.SelectedNode?.Tag, InstanceRef)
        End Function

        ' --------------------------------------------------------
        '  Probe grid (fleet / node scope)
        ' --------------------------------------------------------

        Private Sub BuildProbePanel()
            _probePanel = New Panel() With {.Dock = DockStyle.Fill}

            Dim top As New FlowLayoutPanel() With {
                .Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(4)
            }
            _probeButton = New Button() With {.Text = "&Probe All", .AutoSize = True}
            AddHandler _probeButton.Click, AddressOf OnProbeAll
            _probeStatus = New Label() With {.AutoSize = True, .Padding = New Padding(8, 6, 0, 0)}
            top.Controls.Add(_probeButton)
            top.Controls.Add(_probeStatus)

            _probeList = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .GridLines = True
            }
            _probeList.Columns.Add("Node", 150)
            _probeList.Columns.Add("Address", 150)
            _probeList.Columns.Add("Reachable", 70)
            _probeList.Columns.Add("Latency", 60)
            _probeList.Columns.Add("Build", 110)
            _probeList.Columns.Add("Protocol", 60)
            _probeList.Columns.Add("Contracts", 65)
            _probeList.Columns.Add("Platform", 70)
            _probeList.Columns.Add("EventStream", 80)
            _probeList.Columns.Add("TLS", 110)

            _probePanel.Controls.Add(_probeList)
            _probePanel.Controls.Add(top)
        End Sub

        Private Async Sub OnProbeAll(sender As Object, e As EventArgs)
            Dim nodes = AllNodeRefs()
            _probeButton.Enabled = False
            _probeStatus.Text = "Probing..."
            _probeList.Items.Clear()

            Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
            Dim rows As New List(Of ListViewItem)

            ' One task per attached node; detached nodes get a
            ' static greyed row. Probes run concurrently — a slow
            ' or dead node must not serialize the sweep.
            Dim tasks As New List(Of Task(Of ListViewItem))
            For Each nref In nodes
                If Not nref.Attached Then
                    Dim item As New ListViewItem(nref.DisplayName)
                    item.SubItems.Add($"{nref.HostAddress}:{nref.Port}")
                    item.SubItems.Add("detached")
                    item.ForeColor = SystemColors.GrayText
                    rows.Add(item)
                    Continue For
                End If
                tasks.Add(ProbeOneAsync(factory, nref))
            Next

            Try
                Dim probed = Await Task.WhenAll(tasks)
                rows.AddRange(probed)
            Catch
                ' Individual probes never throw (ProbeOneAsync
                ' catches); defensive only.
            End Try

            _probeList.BeginUpdate()
            For Each r In rows.OrderBy(Function(x) x.Text, StringComparer.OrdinalIgnoreCase)
                _probeList.Items.Add(r)
            Next
            _probeList.EndUpdate()
            _probeStatus.Text = $"Probed {tasks.Count} node(s) at {DateTime.Now:HH:mm:ss}"
            _probeButton.Enabled = True
        End Sub

        Private Async Function ProbeOneAsync(factory As NodeHttpClientFactory,
                                             nref As NodeRef) As Task(Of ListViewItem)
            Dim item As New ListViewItem(nref.DisplayName)
            item.SubItems.Add($"{nref.HostAddress}:{nref.Port}")
            Try
                Dim client = factory.GetClient(nref.NodeId, nref.HostAddress, nref.Port, nref.AuthToken)
                Dim sw = Diagnostics.Stopwatch.StartNew()
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim v = Await client.GetApiVersionAsync(force:=True, cancellation:=cts.Token)
                    sw.Stop()
                    item.SubItems.Add("yes")
                    item.SubItems.Add($"{sw.ElapsedMilliseconds} ms")
                    item.SubItems.Add(If(String.IsNullOrEmpty(v.Build), v.Version, v.Build))
                    item.SubItems.Add(v.ProtocolVersion.ToString())
                    item.SubItems.Add(v.ContractsVersion.ToString())
                    item.SubItems.Add(v.Platform.ToString())
                    item.SubItems.Add(If(v.EventStreamSupported, "yes", "no"))
                    ' Single-port plan: "shared" = HTTPS sniffed on the
                    ' listen port, "alt :N" = dedicated HTTPS listener,
                    ' "HTTP off" appended when plaintext is disabled.
                    Dim tlsText As String
                    If Not v.TlsSupported Then
                        tlsText = "n/a"
                    ElseIf Not v.TlsEnabled Then
                        tlsText = "off"
                    ElseIf v.SinglePort OrElse v.TlsPort = 0 OrElse v.TlsPort = nref.Port Then
                        tlsText = "shared"
                    Else
                        tlsText = $"alt :{v.TlsPort}"
                    End If
                    item.SubItems.Add(tlsText)
                    ' Version-skew highlight: anything not matching
                    ' THIS manager's contract stands out.
                    If v.ProtocolVersion <> NodeApiContract.ProtocolVersion Then
                        item.ForeColor = Color.DarkOrange
                    End If
                End Using
            Catch ex As Exception
                item.SubItems.Add("NO")
                item.ForeColor = Color.Firebrick
                item.ToolTipText = ex.Message
            End Try
            Return item
        End Function

        ' --------------------------------------------------------
        '  Instance view (sources + stream health)
        ' --------------------------------------------------------

        Private Sub BuildInstancePanel()
            _instancePanel = New Panel() With {.Dock = DockStyle.Fill}
            _instanceTabs = New TabControl() With {.Dock = DockStyle.Fill}

            ' ---- Event sources tab ----
            Dim sourcesTab As New TabPage("Event sources")
            Dim srcTop As New FlowLayoutPanel() With {
                .Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(4)
            }
            _sourcesRefresh = New Button() With {.Text = "&Refresh", .AutoSize = True}
            AddHandler _sourcesRefresh.Click, Async Sub() Await RefreshSourcesAsync(SelectedInstanceRef())
            _sourcesStatus = New Label() With {.AutoSize = True, .Padding = New Padding(8, 6, 0, 0)}
            srcTop.Controls.Add(_sourcesRefresh)
            srcTop.Controls.Add(_sourcesStatus)

            _sourcesList = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .GridLines = True
            }
            _sourcesList.Columns.Add("Source", 110)
            _sourcesList.Columns.Add("Appended", 80)
            _sourcesList.Columns.Add("Suppressed", 80)
            _sourcesList.Columns.Add("Owned kinds", 220)
            ' Wave 2: informational — kinds this source has RULES
            ' for, distinct from enforced ownership.
            _sourcesList.Columns.Add("Parses kinds", 220)

            sourcesTab.Controls.Add(_sourcesList)
            sourcesTab.Controls.Add(srcTop)

            ' ---- Stream health tab ----
            Dim streamTab As New TabPage("Stream health")
            Dim strTop As New FlowLayoutPanel() With {
                .Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(4)
            }
            _streamRefresh = New Button() With {.Text = "&Refresh", .AutoSize = True}
            AddHandler _streamRefresh.Click, Sub() RefreshStreamHealth(SelectedInstanceRef())
            strTop.Controls.Add(_streamRefresh)

            _streamHealthList = New ListView() With {
                .Dock = DockStyle.Fill,
                .View = View.Details,
                .FullRowSelect = True,
                .GridLines = True,
                .HeaderStyle = ColumnHeaderStyle.None
            }
            _streamHealthList.Columns.Add("Key", 200)
            _streamHealthList.Columns.Add("Value", 420)

            streamTab.Controls.Add(_streamHealthList)
            streamTab.Controls.Add(strTop)

            ' ---- Wave 2 tabs (2026-08-15). Each loads on its
            ' Refresh button AND when its tab is selected; all
            ' degrade to a status line on 404 (older node). ----

            ' Watermarks
            Dim wmTab As New TabPage("Watermarks")
            Dim wmTop As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(4)}
            Dim wmRefresh As New Button() With {.Text = "&Refresh", .AutoSize = True}
            AddHandler wmRefresh.Click, Async Sub() Await RefreshWatermarksAsync(SelectedInstanceRef())
            _watermarksStatus = New Label() With {.AutoSize = True, .Padding = New Padding(8, 6, 0, 0)}
            wmTop.Controls.Add(wmRefresh) : wmTop.Controls.Add(_watermarksStatus)
            _watermarksList = New ListView() With {
                .Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True, .GridLines = True}
            _watermarksList.Columns.Add("Kind", 120)
            _watermarksList.Columns.Add("Purged through (UTC)", 220)
            _watermarksList.Columns.Add("Blocking?", 260)
            _watermarksList.Columns.Add("Events", 70)
            _watermarksList.Columns.Add("Last event (UTC)", 220)
            wmTab.Controls.Add(_watermarksList) : wmTab.Controls.Add(wmTop)

            ' Parse trace
            Dim trTab As New TabPage("Parse trace")
            Dim trTop As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(4)}
            Dim trRefresh As New Button() With {.Text = "&Refresh", .AutoSize = True}
            AddHandler trRefresh.Click, Async Sub() Await RefreshTraceAsync(SelectedInstanceRef(), rearm:=False)
            Dim trRearm As New Button() With {.Text = "Re-&arm log trace", .AutoSize = True}
            AddHandler trRearm.Click, Async Sub() Await RefreshTraceAsync(SelectedInstanceRef(), rearm:=True)
            _traceStatus = New Label() With {.AutoSize = True, .Padding = New Padding(8, 6, 0, 0)}
            trTop.Controls.Add(trRefresh) : trTop.Controls.Add(trRearm) : trTop.Controls.Add(_traceStatus)
            _traceList = New ListView() With {
                .Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True, .GridLines = True}
            _traceList.Columns.Add("Time (UTC)", 130)
            _traceList.Columns.Add("Source", 70)
            _traceList.Columns.Add("Rule", 150)
            _traceList.Columns.Add("Outcome", 220)
            _traceList.Columns.Add("Line", 520)
            trTab.Controls.Add(_traceList) : trTab.Controls.Add(trTop)

            ' Rule test (dry-run)
            Dim ttTab As New TabPage("Rule test")
            Dim ttTop As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .Height = 62, .Padding = New Padding(4)}
            _testLineBox = New TextBox() With {.Width = 640}
            Dim ttRun As New Button() With {.Text = "&Test line", .AutoSize = True}
            AddHandler ttRun.Click, Async Sub() Await RunTestLineAsync(SelectedInstanceRef())
            _testStatus = New Label() With {.AutoSize = True, .Padding = New Padding(8, 6, 0, 0)}
            ttTop.Controls.Add(New Label() With {.Text = "Console line:", .AutoSize = True, .Padding = New Padding(0, 6, 0, 0)})
            ttTop.Controls.Add(_testLineBox) : ttTop.Controls.Add(ttRun) : ttTop.Controls.Add(_testStatus)
            _testResultsList = New ListView() With {
                .Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True, .GridLines = True}
            _testResultsList.Columns.Add("Rule", 220)
            _testResultsList.Columns.Add("Kind", 120)
            _testResultsList.Columns.Add("Matched", 70)
            _testResultsList.Columns.Add("Captures", 560)
            ttTab.Controls.Add(_testResultsList) : ttTab.Controls.Add(ttTop)

            ' Boot state
            Dim btTab As New TabPage("Boot state")
            Dim btTop As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(4)}
            Dim btRefresh As New Button() With {.Text = "&Refresh", .AutoSize = True}
            AddHandler btRefresh.Click, Async Sub() Await RefreshBootStateAsync(SelectedInstanceRef())
            _bootStatus = New Label() With {.AutoSize = True, .Padding = New Padding(8, 6, 0, 0)}
            btTop.Controls.Add(btRefresh) : btTop.Controls.Add(_bootStatus)
            _bootList = New ListView() With {
                .Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True, .GridLines = True,
                .HeaderStyle = ColumnHeaderStyle.None}
            _bootList.Columns.Add("Key", 220)
            _bootList.Columns.Add("Value", 420)
            btTab.Controls.Add(_bootList) : btTab.Controls.Add(btTop)

            ' Shim link
            Dim shTab As New TabPage("Shim link")
            Dim shTop As New FlowLayoutPanel() With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(4)}
            Dim shRefresh As New Button() With {.Text = "&Refresh", .AutoSize = True}
            AddHandler shRefresh.Click, Async Sub() Await RefreshShimStateAsync(SelectedInstanceRef())
            _shimStatus = New Label() With {.AutoSize = True, .Padding = New Padding(8, 6, 0, 0)}
            shTop.Controls.Add(shRefresh) : shTop.Controls.Add(_shimStatus)
            _shimList = New ListView() With {
                .Dock = DockStyle.Fill, .View = View.Details, .FullRowSelect = True, .GridLines = True,
                .HeaderStyle = ColumnHeaderStyle.None}
            _shimList.Columns.Add("Key", 220)
            _shimList.Columns.Add("Value", 420)
            shTab.Controls.Add(_shimList) : shTab.Controls.Add(shTop)

            _instanceTabs.TabPages.Add(sourcesTab)
            _instanceTabs.TabPages.Add(streamTab)
            _instanceTabs.TabPages.Add(wmTab)
            _instanceTabs.TabPages.Add(trTab)
            _instanceTabs.TabPages.Add(ttTab)
            _instanceTabs.TabPages.Add(btTab)
            _instanceTabs.TabPages.Add(shTab)

            ' Auto-refresh a Wave 2 tab when it becomes visible.
            AddHandler _instanceTabs.SelectedIndexChanged,
                Async Sub()
                    Dim iref = SelectedInstanceRef()
                    If iref Is Nothing Then Return
                    Select Case _instanceTabs.SelectedTab?.Text
                        Case "Watermarks" : Await RefreshWatermarksAsync(iref)
                        Case "Parse trace" : Await RefreshTraceAsync(iref, rearm:=False)
                        Case "Boot state" : Await RefreshBootStateAsync(iref)
                        Case "Shim link" : Await RefreshShimStateAsync(iref)
                    End Select
                End Sub

            _instancePanel.Controls.Add(_instanceTabs)
        End Sub

        ' --------------------------------------------------------
        '  Wave 2 refreshers. Shared shape: clear, call, fill;
        '  NotFound → "Not supported by this node"; any other
        '  failure → status line. Generation guard as elsewhere.
        ' --------------------------------------------------------

        Private Function ClientFor(iref As InstanceRef) As INodeClient
            If iref Is Nothing Then Return Nothing
            Dim im = ManagerProgram.Services.GetService(Of InstanceManager)()
            Return im?.GetNodeClientForInstance(iref.InstanceId)
        End Function

        Private Shared Function IsNotFound(ex As Exception) As Boolean
            Dim api = TryCast(ex, NodeApiException)
            Return api IsNot Nothing AndAlso api.StatusCode.HasValue AndAlso
                   api.StatusCode.Value = Net.HttpStatusCode.NotFound
        End Function

        Private Async Function RefreshWatermarksAsync(iref As InstanceRef) As Task
            If iref Is Nothing Then Return
            Dim gen = _selectionGeneration
            _watermarksList.Items.Clear()
            _watermarksStatus.Text = "Loading..."
            Try
                Dim client = ClientFor(iref)
                If client Is Nothing Then _watermarksStatus.Text = "No node client." : Return
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim wm = Await client.GetEventWatermarksAsync(iref.InstanceId, cts.Token)
                    If gen <> _selectionGeneration Then Return
                    ' Merge per kind: watermark row + count row.
                    Dim counts As New Dictionary(Of String, EventKindCount)(StringComparer.Ordinal)
                    If wm.EventCounts IsNot Nothing Then
                        For Each c In wm.EventCounts
                            counts(c.KindName) = c
                        Next
                    End If
                    Dim shownKinds As New HashSet(Of String)(StringComparer.Ordinal)
                    _watermarksList.BeginUpdate()
                    If wm.Watermarks IsNot Nothing Then
                        For Each w In wm.Watermarks
                            shownKinds.Add(w.KindName)
                            Dim item As New ListViewItem(w.KindName)
                            item.SubItems.Add(w.PurgedThroughUtc)
                            item.SubItems.Add(If(w.IsFuture,
                                "FUTURE — blocks ALL new events of this kind", ""))
                            Dim c As EventKindCount = Nothing
                            counts.TryGetValue(w.KindName, c)
                            item.SubItems.Add(If(c IsNot Nothing, c.Count.ToString(), "0"))
                            item.SubItems.Add(If(c?.LastEventUtc, ""))
                            If w.IsFuture Then item.ForeColor = Color.Firebrick
                            _watermarksList.Items.Add(item)
                        Next
                    End If
                    For Each kvp In counts
                        If shownKinds.Contains(kvp.Key) Then Continue For
                        Dim item As New ListViewItem(kvp.Key)
                        item.SubItems.Add("(no watermark)")
                        item.SubItems.Add("")
                        item.SubItems.Add(kvp.Value.Count.ToString())
                        item.SubItems.Add(If(kvp.Value.LastEventUtc, ""))
                        _watermarksList.Items.Add(item)
                    Next
                    _watermarksList.EndUpdate()
                    _watermarksStatus.Text = $"{_watermarksList.Items.Count} row(s) — {DateTime.Now:HH:mm:ss}"
                End Using
            Catch ex As Exception When IsNotFound(ex)
                If gen = _selectionGeneration Then _watermarksStatus.Text = "Not supported by this node."
            Catch ex As Exception
                If gen = _selectionGeneration Then _watermarksStatus.Text = $"Failed: {ex.Message}"
            End Try
        End Function

        Private Async Function RefreshTraceAsync(iref As InstanceRef, rearm As Boolean) As Task
            If iref Is Nothing Then Return
            Dim gen = _selectionGeneration
            _traceStatus.Text = If(rearm, "Re-arming...", "Loading...")
            Try
                Dim client = ClientFor(iref)
                If client Is Nothing Then _traceStatus.Text = "No node client." : Return
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim tr = If(rearm,
                        Await client.RearmParseTraceAsync(iref.InstanceId, cts.Token),
                        Await client.GetParseTraceAsync(iref.InstanceId, cts.Token))
                    If gen <> _selectionGeneration Then Return
                    _traceList.BeginUpdate()
                    _traceList.Items.Clear()
                    If tr.Entries IsNot Nothing Then
                        ' Newest first.
                        For i = tr.Entries.Count - 1 To 0 Step -1
                            Dim e = tr.Entries(i)
                            Dim item As New ListViewItem(e.AtUtc.ToString("HH:mm:ss.fff"))
                            item.SubItems.Add(If(e.SourceId, ""))
                            item.SubItems.Add(If(e.RuleName, "(none)"))
                            item.SubItems.Add(If(e.Outcome, ""))
                            item.SubItems.Add(If(e.Line, ""))
                            If e.RuleName IsNot Nothing Then
                                item.Font = New Font(_traceList.Font, FontStyle.Bold)
                            End If
                            _traceList.Items.Add(item)
                        Next
                    End If
                    _traceList.EndUpdate()
                    _traceStatus.Text =
                        $"{_traceList.Items.Count} line(s), log budget {tr.LogBudgetRemaining}" &
                        If(rearm, " (re-armed)", "") & $" — {DateTime.Now:HH:mm:ss}"
                End Using
            Catch ex As Exception When IsNotFound(ex)
                If gen = _selectionGeneration Then _traceStatus.Text = "Not supported by this node."
            Catch ex As Exception
                If gen = _selectionGeneration Then _traceStatus.Text = $"Failed: {ex.Message}"
            End Try
        End Function

        Private Async Function RunTestLineAsync(iref As InstanceRef) As Task
            If iref Is Nothing Then Return
            Dim line = _testLineBox.Text
            If String.IsNullOrWhiteSpace(line) Then
                _testStatus.Text = "Enter a console line first."
                Return
            End If
            Dim gen = _selectionGeneration
            _testResultsList.Items.Clear()
            _testStatus.Text = "Running..."
            Try
                Dim client = ClientFor(iref)
                If client Is Nothing Then _testStatus.Text = "No node client." : Return
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim res = Await client.TestParseLineAsync(
                        iref.InstanceId,
                        New RuleDryRunRequest With {.Line = line},
                        cts.Token)
                    If gen <> _selectionGeneration Then Return
                    Dim matched = 0
                    _testResultsList.BeginUpdate()
                    If res.Results IsNot Nothing Then
                        For Each r In res.Results
                            Dim item As New ListViewItem(If(r.RuleName, ""))
                            item.SubItems.Add(If(r.KindName, ""))
                            item.SubItems.Add(If(r.Matched, "YES", ""))
                            Dim caps = ""
                            If r.Captures IsNot Nothing AndAlso r.Captures.Count > 0 Then
                                Dim parts As New List(Of String)
                                For Each kvp In r.Captures
                                    parts.Add($"{kvp.Key}={kvp.Value}")
                                Next
                                caps = String.Join("  ", parts)
                            End If
                            item.SubItems.Add(caps)
                            If r.Matched Then
                                matched += 1
                                item.Font = New Font(_testResultsList.Font, FontStyle.Bold)
                            End If
                            _testResultsList.Items.Add(item)
                        Next
                    End If
                    _testResultsList.EndUpdate()
                    _testStatus.Text = $"{matched} rule(s) matched of {_testResultsList.Items.Count}"
                End Using
            Catch ex As Exception When IsNotFound(ex)
                If gen = _selectionGeneration Then _testStatus.Text = "Not supported by this node."
            Catch ex As Exception
                If gen = _selectionGeneration Then _testStatus.Text = $"Failed: {ex.Message}"
            End Try
        End Function

        Private Async Function RefreshBootStateAsync(iref As InstanceRef) As Task
            If iref Is Nothing Then Return
            Dim gen = _selectionGeneration
            _bootList.Items.Clear()
            _bootStatus.Text = "Loading..."
            Try
                Dim client = ClientFor(iref)
                If client Is Nothing Then _bootStatus.Text = "No node client." : Return
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim b = Await client.GetBootStateAsync(iref.InstanceId, cts.Token)
                    If gen <> _selectionGeneration Then Return
                    Dim addRow = Sub(k As String, v As String)
                                     Dim item As New ListViewItem(k)
                                     item.SubItems.Add(If(v, ""))
                                     _bootList.Items.Add(item)
                                 End Sub
                    addRow("Tracked in memory", If(b.Tracked, $"yes ({b.TrackedState})", "no"))
                    addRow("Snapshot row", If(b.SnapshotExists, $"yes ({b.SnapshotState})", "no"))
                    addRow("Parked (autostart armed shape)", If(b.Parked, "YES", "no"))
                    addRow("Recover state on node restart", If(b.ResurrectOnBoot, "ON", "off"))
                    addRow("Auto start with node", If(b.AutoStartOnBoot, "ON", "off"))
                    addRow("Stop intent pending", If(b.StopIntentPending, "YES", "no"))
                    addRow("Shim ring seq (snapshot)", b.ShimRingSeq.ToString())
                    If b.FlapAttemptCount > 0 Then
                        addRow("Flap guard", $"{b.FlapAttemptCount} attempt(s), last {b.FlapLastAttemptUtc:u}" &
                               If(b.FlapAttemptCount >= 3, "  << SUPPRESSED until 10 quiet minutes", ""))
                    Else
                        addRow("Flap guard", "clear")
                    End If
                    _bootStatus.Text = $"Refreshed {DateTime.Now:HH:mm:ss}"
                End Using
            Catch ex As Exception When IsNotFound(ex)
                If gen = _selectionGeneration Then _bootStatus.Text = "Not supported by this node."
            Catch ex As Exception
                If gen = _selectionGeneration Then _bootStatus.Text = $"Failed: {ex.Message}"
            End Try
        End Function

        Private Async Function RefreshShimStateAsync(iref As InstanceRef) As Task
            If iref Is Nothing Then Return
            Dim gen = _selectionGeneration
            _shimList.Items.Clear()
            _shimStatus.Text = "Loading..."
            Try
                Dim client = ClientFor(iref)
                If client Is Nothing Then _shimStatus.Text = "No node client." : Return
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim s = Await client.GetShimStateAsync(iref.InstanceId, cts.Token)
                    If gen <> _selectionGeneration Then Return
                    Dim addRow = Sub(k As String, v As String)
                                     Dim item As New ListViewItem(k)
                                     item.SubItems.Add(If(v, ""))
                                     _shimList.Items.Add(item)
                                 End Sub
                    addRow("Tracked in memory", If(s.Tracked, "yes", "no"))
                    addRow("Execution mode", If(s.ExecutionMode, "(unknown)"))
                    addRow("Spawn strategy", If(s.Strategy, "(unknown)"))
                    addRow("Shim PID", If(s.ShimPid > 0, s.ShimPid.ToString(), "-"))
                    addRow("Shim endpoint", If(s.ShimEndpoint, "-"))
                    addRow("Shim protocol", If(s.ShimProtocolVersion > 0, $"v{s.ShimProtocolVersion}", "-"))
                    addRow("Session attached", If(s.Attached, "yes", "no"))
                    addRow("Consumed ring seq (live)", s.ConsumedRingSeq.ToString())
                    addRow("Snapshot ring seq (next adopt replays after)", s.SnapshotRingSeq.ToString())
                    _shimStatus.Text = $"Refreshed {DateTime.Now:HH:mm:ss}"
                End Using
            Catch ex As Exception When IsNotFound(ex)
                If gen = _selectionGeneration Then _shimStatus.Text = "Not supported by this node."
            Catch ex As Exception
                If gen = _selectionGeneration Then _shimStatus.Text = $"Failed: {ex.Message}"
            End Try
        End Function

        Private Async Sub RefreshInstanceView(iref As InstanceRef)
            RefreshStreamHealth(iref)
            Await RefreshSourcesAsync(iref)
        End Sub

        Private Async Function RefreshSourcesAsync(iref As InstanceRef) As Task
            If iref Is Nothing Then Return
            Dim gen = _selectionGeneration
            _sourcesList.Items.Clear()
            _sourcesStatus.Text = "Loading..."
            Try
                Dim im = ManagerProgram.Services.GetService(Of InstanceManager)()
                Dim client = im?.GetNodeClientForInstance(iref.InstanceId)
                If client Is Nothing Then
                    _sourcesStatus.Text = "No node client available for this instance."
                    Return
                End If
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(8))
                    Dim sources = Await client.GetEventSourceDiagnosticsAsync(iref.InstanceId, cts.Token)
                    If gen <> _selectionGeneration Then Return
                    _sourcesList.BeginUpdate()
                    For Each s In sources.OrderBy(Function(x) x.SourceId, StringComparer.OrdinalIgnoreCase)
                        Dim item As New ListViewItem(s.SourceId)
                        item.SubItems.Add(s.Appended.ToString())
                        item.SubItems.Add(s.Suppressed.ToString())
                        item.SubItems.Add(If(s.OwnedKinds Is Nothing OrElse s.OwnedKinds.Count = 0,
                                             "", String.Join(", ", s.OwnedKinds)))
                        item.SubItems.Add(If(s.ParsesKinds Is Nothing OrElse s.ParsesKinds.Count = 0,
                                             "", String.Join(", ", s.ParsesKinds)))
                        ' A source that owns kinds is the authority —
                        ' make it visually primary.
                        If s.OwnedKinds IsNot Nothing AndAlso s.OwnedKinds.Count > 0 Then
                            item.Font = New Font(_sourcesList.Font, FontStyle.Bold)
                        End If
                        _sourcesList.Items.Add(item)
                    Next
                    _sourcesList.EndUpdate()
                    _sourcesStatus.Text = $"{sources.Count} source(s), counters since node start — {DateTime.Now:HH:mm:ss}"
                End Using
            Catch ex As NodeApiException When ex.StatusCode.HasValue AndAlso
                                              ex.StatusCode.Value = Net.HttpStatusCode.NotFound
                If gen = _selectionGeneration Then
                    _sourcesStatus.Text = "Not supported by this node (pre-stream version)."
                End If
            Catch ex As Exception
                If gen = _selectionGeneration Then
                    _sourcesStatus.Text = $"Failed: {ex.Message}"
                End If
            End Try
        End Function

        Private Sub RefreshStreamHealth(iref As InstanceRef)
            If iref Is Nothing Then Return
            _streamHealthList.Items.Clear()

            Dim addRow = Sub(k As String, v As String)
                             Dim item As New ListViewItem(k)
                             item.SubItems.Add(If(v, ""))
                             _streamHealthList.Items.Add(item)
                         End Sub

            addRow("Instance", iref.DisplayName)
            addRow("Instance id", iref.InstanceId)
            addRow("Node", $"{iref.Node.DisplayName} ({iref.Node.HostAddress}:{iref.Node.Port})")

            Try
                Dim sync = ManagerProgram.Services.GetService(Of EventSyncService)()
                If sync IsNot Nothing Then
                    Dim state = sync.GetState(iref.InstanceId)
                    addRow("Event sync", state.ToString())
                    addRow("Legacy ingestion", If(sync.SuppressesLegacyIngestion(iref.InstanceId),
                                                  "suppressed (stream owns join/leave)",
                                                  "active (legacy projections own join/leave)"))
                Else
                    addRow("Event sync", "(service unavailable)")
                End If
            Catch ex As Exception
                addRow("Event sync", $"(failed: {ex.Message})")
            End Try

            ' Sync cursor: last event seq committed by the Manager's
            ' stream sync for this instance. Grows as events arrive;
            ' compare against a fresh sources Appended jump to see
            ' whether the Manager is keeping up.
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim raw = db.GetSetting(EventSyncService.CursorKey(iref.InstanceId), Nothing)
                    addRow("Sync cursor seq", If(String.IsNullOrEmpty(raw), "(none)", raw))
                End Using
            Catch ex As Exception
                addRow("Sync cursor seq", $"(failed: {ex.Message})")
            End Try
        End Sub

    End Class

End Namespace
