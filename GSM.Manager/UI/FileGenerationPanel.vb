Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Manager
Imports GSM.Manager.Core
Imports GSM.Manager.Data
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  FileGenerationPanel — generic schema-driven generation UI
'
'  Phase 4c-3 (generic). Hosted inside a TabPage that
'  ManagedFilesPanel creates on demand when the user clicks
'  the plugin's Generate button. Sibling to the directory's
'  Saves tab — opening generation doesn't block navigation to
'  other tabs (Logs, Configuration, etc.) so the user can
'  monitor a running instance while a save is being generated
'  for it.
'
'  This panel knows nothing about maps, presets, seeds, or
'  any other domain concept. It:
'
'    1. Asks the plugin (via IFileGenerationProvider) for a
'       schema describing what to ask the user.
'    2. Renders that schema with SchemaFormBuilder \u2014 the same
'       builder that powers Edit Instance.
'    3. On Submit, collects the values via the schema's
'       ValueExtractor, hands them to the plugin's
'       BuildGenerationSteps, which returns a
'       GenerationStepBundle.
'    4. Ships the bundle to the node via
'       NodeHttpClient.GenerateMapAsync (endpoint name kept
'       for wire stability \u2014 the underlying machinery is
'       fully generic).
'    5. Reports success or failure with reusable post-success
'       buttons (Show in Files / Generate Another).
'
'  Anything Factorio-specific (preset list, seed validation,
'  filename normalisation, JSON blobs) lives in the plugin's
'  IFileGenerationProvider implementation. Adding a new
'  generation-style operation to a future plugin requires
'  zero changes to this file.
'
'  Lifecycle:
'    - ManagedFilesPanel constructs us, passes onClose and
'      onSuccess callbacks plus the resolved tab title.
'    - We don't manage the host TabPage ourselves; the
'      callbacks belong to the parent that knows about both
'      the source tab and the TabControl.
'
'  Cancellation: a CancellationTokenSource is disposed on tab
'  teardown so a slow node doesn't keep the request alive
'  after the user closes the tab. Cancellation propagates
'  through the one-shot HttpClient that
'  NodeHttpClient.GenerateMapAsync creates.
' ============================================================

Namespace GSM.Manager.UI

    Public Class FileGenerationPanel
        Inherits UserControl

        Private ReadOnly _instanceId As String
        Private ReadOnly _onClose As Action
        Private ReadOnly _onSuccess As Action
        Private ReadOnly _tabTitle As String

        Private _schemaResult As SchemaFormResult
        Private _formHost As Panel
        Private _generateButton As Button
        Private _cancelButton As Button
        Private _showInFilesButton As Button
        Private _generateAnotherButton As Button
        Private _statusLabel As Label

        ' True while a generation request is in flight. Disables
        ' Generate / form inputs and switches Cancel into "abort
        ' the in-flight request" mode.
        Private _opInFlight As Boolean

        Private _genCts As CancellationTokenSource

        ''' <summary>
        ''' Construct a panel for the given instance with caller-
        ''' supplied lifecycle callbacks. onClose fires when the
        ''' user dismisses the panel without success (Cancel button
        ''' before generation, or after a failure). onSuccess fires
        ''' when generation completed and the user clicked the
        ''' "Show in Files" button. Either may be Nothing.
        ''' </summary>
        Public Sub New(instanceId As String,
                        tabTitle As String,
                        onClose As Action,
                        onSuccess As Action)
            _instanceId = instanceId
            _tabTitle = If(String.IsNullOrEmpty(tabTitle), "Generate File", tabTitle)
            _onClose = onClose
            _onSuccess = onSuccess
            InitializeControls()
            LoadSchema()
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _genCts IsNot Nothing Then
                Try
                    _genCts.Cancel()
                    _genCts.Dispose()
                Catch
                End Try
                _genCts = Nothing
            End If
            MyBase.Dispose(disposing)
        End Sub

        Private Sub InitializeControls()
            Me.Padding = New Padding(0)

            Dim header As New Label() With {
                .Text = _tabTitle,
                .Font = New Font("Segoe UI", 14, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(20, 15)
            }
            Dim subtitle As New Label() With {
                .Text = "Fill in the fields below and click Generate. The operation runs on the node and can take a few seconds to a few minutes.",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.Gray,
                .AutoSize = False,
                .Size = New Size(620, 32),
                .Location = New Point(22, 45)
            }
            Me.Controls.AddRange(New Control() {header, subtitle})

            ' Form host \u2014 fills the area between the header and
            ' the action buttons, scrolls if the schema's tall.
            ' Y positions for buttons/status are computed below
            ' once we know the host height.
            Const FormY As Integer = 90
            Const ButtonStripHeight As Integer = 50
            Const StatusHeight As Integer = 22

            _formHost = New Panel() With {
                .Location = New Point(20, FormY),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                          AnchorStyles.Right Or AnchorStyles.Bottom,
                .AutoScroll = True
            }
            ' Width relative to the panel; height relative to bottom.
            ' Anchor flags above keep this in sync as the parent resizes.
            _formHost.Size = New Size(Me.Width - 40,
                                       Math.Max(100, Me.Height - FormY - ButtonStripHeight - StatusHeight - 10))
            Me.Controls.Add(_formHost)

            ' Bottom strip \u2014 buttons + status. Anchored to the
            ' bottom-left so a tall schema growing the form panel
            ' doesn't push them off-screen.
            _generateButton = New Button() With {
                .Text = "Generate",
                .Size = New Size(120, 32)
            }
            AddHandler _generateButton.Click, Sub(s, e) GenerateClicked()

            _cancelButton = New Button() With {
                .Text = "Cancel",
                .Size = New Size(100, 32)
            }
            AddHandler _cancelButton.Click, Sub(s, e) CancelClicked()

            _showInFilesButton = New Button() With {
                .Text = "Show in Files",
                .Size = New Size(140, 32),
                .Visible = False
            }
            AddHandler _showInFilesButton.Click, Sub(s, e) ShowInFilesClicked()

            _generateAnotherButton = New Button() With {
                .Text = "Generate Another",
                .Size = New Size(160, 32),
                .Visible = False
            }
            AddHandler _generateAnotherButton.Click, Sub(s, e) GenerateAnotherClicked()

            _statusLabel = New Label() With {
                .Text = "Loading...",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.Gray,
                .AutoSize = False,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
            }

            Me.Controls.AddRange(New Control() {
                _generateButton, _cancelButton,
                _showInFilesButton, _generateAnotherButton,
                _statusLabel
            })

            AddHandler Me.Resize, Sub(s, e) LayoutBottomStrip()
            LayoutBottomStrip()
        End Sub

        ''' <summary>
        ''' Position the action buttons and status label relative
        ''' to the panel's current size. Called from Resize so the
        ''' bottom strip tracks parent resizes; the form host's
        ''' Anchor handles its own resize. Buttons share the same
        ''' Y so visibility-flip between Generate/Cancel and the
        ''' post-success pair doesn't shuffle the layout.
        ''' </summary>
        Private Sub LayoutBottomStrip()
            If _generateButton Is Nothing OrElse _statusLabel Is Nothing Then Return
            Const StatusHeight As Integer = 22
            Const ButtonStripHeight As Integer = 50

            Dim buttonY = Math.Max(60, Me.Height - StatusHeight - ButtonStripHeight)

            _generateButton.Location = New Point(20, buttonY)
            _cancelButton.Location = New Point(150, buttonY)
            _showInFilesButton.Location = New Point(20, buttonY)
            _generateAnotherButton.Location = New Point(170, buttonY)

            _statusLabel.Location = New Point(20, buttonY + ButtonStripHeight - 12)
            _statusLabel.Size = New Size(Math.Max(200, Me.Width - 40), StatusHeight)

            ' Form host height: between header and bottom strip.
            If _formHost IsNot Nothing Then
                _formHost.Size = New Size(Math.Max(100, Me.Width - 40),
                                           Math.Max(60, buttonY - _formHost.Top - 10))
            End If
        End Sub

        ''' <summary>
        ''' Resolve the plugin's IFileGenerationProvider, fetch its
        ''' schema, render it via SchemaFormBuilder. Failure here
        ''' disables the form and shows an error \u2014 the user can
        ''' close and try again once the issue is resolved.
        ''' </summary>
        Private Sub LoadSchema()
            Try
                Dim provider = ResolveProvider()
                If provider Is Nothing Then
                    SetStatus("File generation isn't available for this instance.", Color.Firebrick)
                    _generateButton.Enabled = False
                    Return
                End If

                Dim instanceConfig = ResolveInstanceConfigForSchema()

                Dim schema As IReadOnlyList(Of ConfigFieldDescriptor) = Nothing
                Try
                    schema = provider.GetGenerationSchema(instanceConfig)
                Catch ex As Exception
                    SetStatus($"Plugin failed to provide schema: {ex.Message}", Color.Firebrick)
                    _generateButton.Enabled = False
                    Return
                End Try

                If schema Is Nothing OrElse schema.Count = 0 Then
                    SetStatus("Plugin returned no schema fields.", Color.Firebrick)
                    _generateButton.Enabled = False
                    Return
                End If

                ' Seed values from defaults \u2014 SchemaFormBuilder
                ' falls back to DefaultValue when currentValues
                ' has no entry for a key, but giving it an empty
                ' dict is the cleanest way to express "fresh form,
                ' use whatever the schema declares as defaults".
                _schemaResult = SchemaFormBuilder.Build(schema, New Dictionary(Of String, String))
                If _schemaResult.Panel IsNot Nothing Then
                    _schemaResult.Panel.Dock = DockStyle.Fill
                    _formHost.Controls.Clear()
                    _formHost.Controls.Add(_schemaResult.Panel)
                End If

                SetStatus("Ready.", Color.Gray)
            Catch ex As Exception
                SetStatus($"Failed to load form: {ex.Message}", Color.Firebrick)
                _generateButton.Enabled = False
            End Try
        End Sub

        ' ====================================================
        '  Generate
        ' ====================================================

        Private Async Sub GenerateClicked()
            If _opInFlight Then Return
            If _schemaResult Is Nothing OrElse _schemaResult.ValueExtractor Is Nothing Then
                SetStatus("Form isn't ready.", Color.Firebrick)
                Return
            End If

            Dim values = _schemaResult.ValueExtractor.Invoke()
            If values Is Nothing Then values = New Dictionary(Of String, String)

            Dim provider = ResolveProvider()
            Dim resolved = ResolveNodeAndConfig()
            If provider Is Nothing OrElse resolved Is Nothing Then
                SetStatus("Could not resolve plugin or node for this instance.", Color.Firebrick)
                Return
            End If

            ' Pre-fetch the node's platform so the plugin's
            ' BuildGenerationSteps can pick the right executable
            ' name (e.g. factorio.exe on Windows vs factorio on
            ' Linux) directly. Cached on the client after the
            ' first call, so this is essentially free on subsequent
            ' generations against the same node.
            Dim nodePlatform = Await NodePlatformResolver.ResolveAsync(resolved.Client, CancellationToken.None)

            ' Hand the form values to the plugin and let it produce
            ' the step bundle. Plugin owns all interpretation \u2014
            ' missing/invalid values surface as InvalidOperationException
            ' (or similar) which we render verbatim to the user.
            Dim minimalConfig As New InstanceConfig With {
                .InstanceId = _instanceId,
                .GameId = resolved.GameId,
                .DisplayName = resolved.InstanceDisplayName,
                .InstallationId = resolved.InstallationId,
                .Platform = nodePlatform
            }

            Dim bundle As GenerationStepBundle = Nothing
            Try
                bundle = provider.BuildGenerationSteps(values, minimalConfig)
            Catch ex As Exception
                SetStatus(ex.Message, Color.Firebrick)
                Return
            End Try

            If bundle Is Nothing OrElse bundle.Steps Is Nothing OrElse bundle.Steps.Count = 0 Then
                SetStatus("Plugin produced no generation steps.", Color.Firebrick)
                Return
            End If

            Dim request As New GenerateMapRequest With {
                .InstallPath = resolved.InstallPath,
                .Steps = bundle.Steps.ToList(),
                .TimeoutSeconds = If(bundle.TimeoutSeconds > 0, bundle.TimeoutSeconds, 600),
                .ExpectedOutputRelativePath = bundle.ExpectedOutputRelativePath
            }

            ' Switch to in-flight UI state. Disable inputs so the
            ' user can't change them mid-request, but keep Cancel
            ' enabled so they can abandon a slow generation.
            _opInFlight = True
            SetFormEnabled(False)
            _cancelButton.Enabled = True

            Dim outputDescription = If(String.IsNullOrEmpty(bundle.ExpectedOutputRelativePath),
                                        "...", $"({bundle.ExpectedOutputRelativePath})")
            SetStatus($"Generating {outputDescription}... this can take up to a few minutes.",
                      Color.DarkOrange)

            _genCts?.Dispose()
            _genCts = New CancellationTokenSource()
            Dim token = _genCts.Token

            Try
                Dim response = Await resolved.Client.GenerateMapAsync(_instanceId, request, token)
                If Me.IsDisposed OrElse token.IsCancellationRequested Then Return

                If response Is Nothing Then
                    ApplyFailureState("Node returned no response.", "")
                    Return
                End If

                If response.Success Then
                    ApplySuccessState(response, bundle.ExpectedOutputRelativePath)
                Else
                    Dim shortError = If(String.IsNullOrEmpty(response.ErrorMessage),
                                         "Generation failed.",
                                         response.ErrorMessage)
                    Dim stepNote = If(response.FailedStepIndex >= 0,
                                       $" (step {response.FailedStepIndex + 1})",
                                       "")
                    ApplyFailureState(shortError & stepNote, response.Output)
                End If
            Catch ex As OperationCanceledException
                If Not Me.IsDisposed Then
                    SetStatus("Generation cancelled.", Color.Gray)
                    _opInFlight = False
                    SetFormEnabled(True)
                End If
            Catch ex As Exception
                If Not Me.IsDisposed Then ApplyFailureState($"Request failed: {ex.Message}", "")
            End Try
        End Sub

        Private Sub ApplySuccessState(response As GenerateMapResponse, expectedOutput As String)
            _opInFlight = False
            SetFormEnabled(False)  ' inputs stay locked; user uses post-success buttons
            _generateButton.Visible = False
            _cancelButton.Visible = False
            _showInFilesButton.Visible = True
            _generateAnotherButton.Visible = True

            Dim outputName = If(String.IsNullOrEmpty(response.OutputRelativePath),
                                 expectedOutput,
                                 response.OutputRelativePath)
            Dim sizeNote = If(response.OutputSizeBytes > 0,
                               $" ({FormatSize(response.OutputSizeBytes)})",
                               "")
            Dim displayName = If(String.IsNullOrEmpty(outputName), "file", outputName)
            SetStatus($"\u2713 Generated {displayName}{sizeNote}.", Color.DarkGreen)
        End Sub

        Private Sub ApplyFailureState(message As String, output As String)
            _opInFlight = False
            SetFormEnabled(True)
            _generateButton.Visible = True
            _cancelButton.Visible = True
            _showInFilesButton.Visible = False
            _generateAnotherButton.Visible = False

            ' If the node returned captured engine output, surface
            ' it in a resizable dialog. The status label below
            ' continues to show a short summary for at-a-glance
            ' recognisability, but the dialog carries the actual
            ' diagnostic content (Factorio's stdout/stderr) that
            ' the user needs to fix the underlying problem. Skip
            ' the dialog when output is empty (transport failures,
            ' validation errors raised before the engine ran)
            ' since the message already says everything there is
            ' to say.
            If Not String.IsNullOrWhiteSpace(output) Then
                ShowGenerationErrorDialog(message, output)
            End If

            ' Show the engine output in the status if present and
            ' the bare message is short \u2014 gives the user something
            ' to act on rather than just "Generation failed."
            If Not String.IsNullOrEmpty(output) AndAlso message.Length < 80 Then
                Dim tail = output.TrimEnd()
                If tail.Length > 200 Then tail = tail.Substring(tail.Length - 200)
                SetStatus($"{message} \u2014 {tail}", Color.Firebrick)
            Else
                SetStatus(message, Color.Firebrick)
            End If
        End Sub

        ''' <summary>
        ''' Show the generation failure in a resizable dialog with
        ''' a multi-line, read-only TextBox so the user can read
        ''' the engine's full stdout/stderr and copy it for a bug
        ''' report. Visual layout follows the same pattern as
        ''' NewInstallationForm's install-error dialog so the
        ''' manager has a consistent look for engine-failure
        ''' surfaces.
        ''' </summary>
        Private Sub ShowGenerationErrorDialog(headline As String, output As String)
            Using dlg As New Form()
                dlg.Text = "Generation Failed"
                dlg.Size = New Size(720, 480)
                dlg.StartPosition = FormStartPosition.CenterParent
                dlg.MinimumSize = New Size(480, 280)
                dlg.FormBorderStyle = FormBorderStyle.Sizable
                dlg.MaximizeBox = True
                dlg.MinimizeBox = False

                Dim icon As New PictureBox() With {
                    .Image = SystemIcons.Warning.ToBitmap(),
                    .SizeMode = PictureBoxSizeMode.AutoSize,
                    .Location = New Point(15, 15)
                }
                dlg.Controls.Add(icon)

                Dim header As New Label() With {
                    .Text = If(String.IsNullOrEmpty(headline), "Generation failed.", headline),
                    .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                    .AutoSize = False,
                    .Size = New Size(610, 36),
                    .Location = New Point(70, 18),
                    .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
                }
                dlg.Controls.Add(header)

                Dim outputLabel As New Label() With {
                    .Text = "Engine output:",
                    .AutoSize = True,
                    .Location = New Point(15, 60),
                    .Anchor = AnchorStyles.Top Or AnchorStyles.Left
                }
                dlg.Controls.Add(outputLabel)

                Dim body As New TextBox() With {
                    .Multiline = True,
                    .ReadOnly = True,
                    .ScrollBars = ScrollBars.Both,
                    .WordWrap = False,
                    .Font = New Font("Consolas", 9.25F),
                    .BackColor = SystemColors.Window,
                    .Text = output,
                    .Location = New Point(15, 85),
                    .Size = New Size(675, 320),
                    .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                              AnchorStyles.Right Or AnchorStyles.Bottom
                }
                ' Scroll to the END rather than the start. Engine
                ' errors typically land at the bottom of the output
                ' (after a banner of init lines that aren't
                ' actionable), so opening at the bottom puts the
                ' diagnostic in front of the user immediately.
                body.Select(body.TextLength, 0)
                body.ScrollToCaret()
                dlg.Controls.Add(body)

                Dim okButton As New Button() With {
                    .Text = "OK",
                    .Size = New Size(90, 28),
                    .Location = New Point(600, 415),
                    .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right,
                    .DialogResult = DialogResult.OK
                }
                dlg.Controls.Add(okButton)
                dlg.AcceptButton = okButton
                dlg.CancelButton = okButton

                dlg.ShowDialog(Me.FindForm())
            End Using
        End Sub

        Private Sub CancelClicked()
            If _opInFlight Then
                ' Trip the CTS \u2014 the awaiting GenerateClicked picks
                ' it up via OperationCanceledException and resets
                ' the form to ready state without firing onClose.
                Try
                    _genCts?.Cancel()
                Catch
                End Try
                Return
            End If
            ' Not in flight \u2014 Cancel just closes the panel.
            _onClose?.Invoke()
        End Sub

        Private Sub ShowInFilesClicked()
            _onSuccess?.Invoke()
        End Sub

        Private Sub GenerateAnotherClicked()
            _generateButton.Visible = True
            _cancelButton.Visible = True
            _showInFilesButton.Visible = False
            _generateAnotherButton.Visible = False
            ' Rebuild the schema from scratch so any plugin-side
            ' state (timestamps, default suggestions) is fresh.
            ' The cheap path would be to just clear ValueExtractor's
            ' inputs, but plugins may have legitimate reasons to
            ' regenerate the schema (e.g. include a count of
            ' previously-generated files).
            _formHost.Controls.Clear()
            _schemaResult = Nothing
            LoadSchema()
            SetFormEnabled(True)
            SetStatus("Ready.", Color.Gray)
        End Sub

        Private Sub SetFormEnabled(enabled As Boolean)
            If _formHost IsNot Nothing Then _formHost.Enabled = enabled
            _generateButton.Enabled = enabled
        End Sub

        Private Sub SetStatus(text As String, color As Color)
            If Me.IsDisposed OrElse _statusLabel Is Nothing Then Return
            _statusLabel.Text = text
            _statusLabel.ForeColor = color
        End Sub

        Private Shared Function FormatSize(bytes As Long) As String
            If bytes < 1024 Then Return $"{bytes} B"
            If bytes < 1024L * 1024L Then Return $"{(bytes / 1024.0):F1} KB"
            If bytes < 1024L * 1024L * 1024L Then Return $"{(bytes / 1048576.0):F1} MB"
            Return $"{(bytes / 1073741824.0):F2} GB"
        End Function

        ' ====================================================
        '  Resolution helpers
        ' ====================================================

        Private Function ResolveProvider() As IFileGenerationProvider
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return Nothing
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then Return Nothing
                    Dim plugin = registry.GetPlugin(instanceEntity.GameId)
                    Return TryCast(plugin, IFileGenerationProvider)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Minimal InstanceConfig for the schema-building call.
        ''' Plugins that need merged install+instance config in
        ''' GetGenerationSchema can pull from CustomFields here \u2014
        ''' for v1 none do, so we keep it light.
        ''' </summary>
        Private Function ResolveInstanceConfigForSchema() As InstanceConfig
            Try
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then
                        Return New InstanceConfig With {.InstanceId = _instanceId}
                    End If
                    Return New InstanceConfig With {
                        .InstanceId = instanceEntity.InstanceId,
                        .GameId = instanceEntity.GameId,
                        .DisplayName = instanceEntity.DisplayName,
                        .InstallationId = instanceEntity.InstallationId
                    }
                End Using
            Catch
                Return New InstanceConfig With {.InstanceId = _instanceId}
            End Try
        End Function

        ''' <summary>
        ''' Resolved bundle: the node client, the install path, and
        ''' the metadata BuildGenerationSteps wants in its
        ''' InstanceConfig argument. Returned by one DB query so
        ''' callers don't open three separate scopes.
        ''' </summary>
        Private Class ResolvedContext
            Public Client As INodeClient
            Public InstallPath As String
            Public GameId As String
            Public InstanceDisplayName As String
            Public InstallationId As String
        End Class

        Private Function ResolveNodeAndConfig() As ResolvedContext
            Try
                Dim factory = ManagerProgram.Services.GetService(Of NodeHttpClientFactory)()
                If factory Is Nothing Then Return Nothing
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then Return Nothing
                    Dim installEntity = db.Installations.Find(instanceEntity.InstallationId)
                    If installEntity Is Nothing Then Return Nothing
                    Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                    If nodeEntity Is Nothing Then Return Nothing
                    Dim client = factory.GetClient(nodeEntity.NodeId,
                                                    nodeEntity.HostAddress,
                                                    nodeEntity.Port,
                                                    nodeEntity.AuthToken)
                    Return New ResolvedContext With {
                        .Client = client,
                        .InstallPath = installEntity.InstallPath,
                        .GameId = instanceEntity.GameId,
                        .InstanceDisplayName = instanceEntity.DisplayName,
                        .InstallationId = instanceEntity.InstallationId
                    }
                End Using
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
