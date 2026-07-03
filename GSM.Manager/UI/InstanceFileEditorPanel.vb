Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Text
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
'  InstanceFileEditorPanel — structured editor for one config file
'
'  Phase 4c-4. Hosted as a tab on InstancePanel for plugins that
'  implement IInstanceFileEditorProvider. The canonical case is
'  Factorio's server-settings.json — the user gets a form with
'  the commonly-edited fields instead of having to hand-edit the
'  JSON.
'
'  Generic shell:
'    1. Resolves the editor's plugin (IInstanceFileEditorProvider)
'       once at construction.
'    2. Downloads the file via INodeClient.DownloadFileAsync —
'       allowedRoots/allowedExtensions auto-derived from
'       editor.RelativePath. If the file 404s we treat it as
'       empty and the schema renders defaults.
'    3. Renders the schema with SchemaFormBuilder.
'    4. Save: collects values via the schema's ValueExtractor,
'       calls plugin's WriteValuesToFile passing the cached
'       last-downloaded text (so unknown fields round-trip),
'       uploads the result.
'    5. Reload: re-downloads from the node, rebuilds the form.
'
'  Nothing about server-settings, JSON, or any specific format
'  lives here. A future plugin that wants an editor for a
'  YAML config file just implements IInstanceFileEditorProvider
'  and the same shell renders it.
' ============================================================

Namespace GSM.Manager.UI

    Public Class InstanceFileEditorPanel
        Inherits UserControl

        Private ReadOnly _instanceId As String
        Private ReadOnly _editor As InstanceFileEditor

        ' Cached file text from the last successful download. Empty
        ' string when the file didn't exist on the node. Passed
        ' verbatim to plugin.WriteValuesToFile on Save so unknown
        ' fields the user added by hand outside the schema round-
        ' trip unchanged.
        Private _lastDownloadedText As String = ""

        Private _schemaResult As SchemaFormResult
        Private _formHost As Panel
        Private _saveButton As Button
        Private _reloadButton As Button
        Private _statusLabel As Label
        Private _pathLabel As Label

        ' True while a download/upload is in flight. Disables both
        ' buttons so a Save mid-Reload (or vice versa) can't
        ' corrupt state.
        Private _opInFlight As Boolean

        ' True once the file is confirmed absent AND the editor is
        ' RequiresExistingFile: the form renders but is disabled and
        ' Save is locked until the server generates the file.
        Private _locked As Boolean

        ' Tripped on Dispose so async resumptions see cancellation
        ' and bail out before touching disposed controls.
        Private _disposeCts As CancellationTokenSource

        Public Sub New(instanceId As String, editor As InstanceFileEditor)
            _instanceId = instanceId
            _editor = editor
            _disposeCts = New CancellationTokenSource()
            InitializeControls()

            ' Fire-and-forget initial load. _unused = ... explicitly
            ' discards the Task so VB doesn't complain about the
            ' un-awaited return; the LoadAsync resumption marshals
            ' back to the UI thread via BeginInvoke.
            Dim _unused = LoadAsync(_disposeCts.Token)
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _disposeCts IsNot Nothing Then
                Try
                    _disposeCts.Cancel()
                    _disposeCts.Dispose()
                Catch
                End Try
                _disposeCts = Nothing
            End If
            MyBase.Dispose(disposing)
        End Sub

        ' ====================================================
        '  Layout
        ' ====================================================

        Private Sub InitializeControls()
            Me.Padding = New Padding(0)

            Dim header As New Label() With {
                .Text = If(_editor.TabTitle, "File Editor"),
                .Font = New Font("Segoe UI", 14, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(20, 15)
            }

            _pathLabel = New Label() With {
                .Text = $"Editing: {_editor.RelativePath}",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.Gray,
                .AutoSize = True,
                .Location = New Point(22, 45)
            }

            Me.Controls.AddRange(New Control() {header, _pathLabel})

            ' Form host \u2014 fills the area between the header and
            ' the action buttons, scrolls if the schema's tall.
            Const FormY As Integer = 75
            _formHost = New Panel() With {
                .Location = New Point(20, FormY),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or
                          AnchorStyles.Right Or AnchorStyles.Bottom,
                .AutoScroll = True
            }
            _formHost.Size = New Size(Math.Max(100, Me.Width - 40), 200)
            Me.Controls.Add(_formHost)

            ' Bottom strip \u2014 buttons + status. Anchored to the
            ' bottom-left so a tall schema growing the form panel
            ' doesn't push them off-screen.
            _saveButton = New Button() With {
                .Text = "Save",
                .Size = New Size(100, 32),
                .Enabled = False
            }
            AddHandler _saveButton.Click, Sub(s, e) SaveClicked()

            _reloadButton = New Button() With {
                .Text = "Reload",
                .Size = New Size(100, 32),
                .Enabled = False
            }
            AddHandler _reloadButton.Click, Sub(s, e) ReloadClicked()

            _statusLabel = New Label() With {
                .Text = "Loading...",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.Gray,
                .AutoSize = False,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
            }

            Me.Controls.AddRange(New Control() {_saveButton, _reloadButton, _statusLabel})

            AddHandler Me.Resize, Sub(s, e) LayoutBottomStrip()
            LayoutBottomStrip()
        End Sub

        ''' <summary>
        ''' Position the action buttons and status label relative
        ''' to the panel's current size. Called from Resize so the
        ''' bottom strip tracks parent resizes; the form host's
        ''' Anchor handles its own resize.
        ''' </summary>
        Private Sub LayoutBottomStrip()
            If _saveButton Is Nothing OrElse _statusLabel Is Nothing Then Return
            Const StatusHeight As Integer = 22
            Const ButtonStripHeight As Integer = 50

            Dim buttonY = Math.Max(60, Me.Height - StatusHeight - ButtonStripHeight)

            _saveButton.Location = New Point(20, buttonY)
            _reloadButton.Location = New Point(130, buttonY)

            _statusLabel.Location = New Point(20, buttonY + ButtonStripHeight - 12)
            _statusLabel.Size = New Size(Math.Max(200, Me.Width - 40), StatusHeight)

            ' Form host height: between header and bottom strip.
            If _formHost IsNot Nothing Then
                _formHost.Size = New Size(Math.Max(100, Me.Width - 40),
                                           Math.Max(60, buttonY - _formHost.Top - 10))
            End If
        End Sub

        ' ====================================================
        '  Load / Reload
        ' ====================================================

        ''' <summary>
        ''' Download the file, ask the plugin to convert it to a
        ''' values dict, render the schema. Called on construction
        ''' and via Reload. Empty fileText on a 404 is fine \u2014 the
        ''' schema's defaults take over.
        ''' </summary>
        Private Async Function LoadAsync(token As CancellationToken) As Task
            If _opInFlight Then Return
            _opInFlight = True
            UpdateButtons()
            SetStatus("Loading...", Color.Gray)

            Dim provider As IInstanceFileEditorProvider = Nothing
            Dim resolved As ResolvedNodeContext = Nothing
            Try
                provider = ResolveProvider()
                resolved = ResolveNodeContext()
                If provider Is Nothing OrElse resolved Is Nothing Then
                    SetStatus("Could not resolve plugin or node for this instance.", Color.Firebrick)
                    Return
                End If

                Dim fileText As String = ""
                Dim fileExisted As Boolean = False
                Try
                    fileText = Await DownloadAsTextAsync(resolved, token)
                    fileExisted = True
                Catch ex As OperationCanceledException
                    Return
                Catch ex As NodeApiException
                    ' 404 \u2192 file doesn't exist yet. Anything else
                    ' is a real error \u2014 surface it but still render
                    ' the form with defaults so the user can save a
                    ' fresh file.
                    If IsNotFound(ex) Then
                        fileText = ""
                        fileExisted = False
                    Else
                        SetStatus($"Failed to load: {ex.Message}", Color.Firebrick)
                        Return
                    End If
                Catch ex As Exception
                    SetStatus($"Failed to load: {ex.Message}", Color.Firebrick)
                    Return
                End Try

                If token.IsCancellationRequested OrElse Me.IsDisposed Then Return

                _lastDownloadedText = fileText

                ' Hand the file text to the plugin to convert into
                ' form values. Plugin returns an empty dict if it
                ' can't make sense of the text \u2014 schema defaults
                ' take over.
                Dim values As Dictionary(Of String, String)
                Try
                    values = provider.ReadFileToValues(_editor.Key, fileText)
                Catch ex As Exception
                    SetStatus($"Plugin failed to parse file: {ex.Message}", Color.Firebrick)
                    Return
                End Try
                If values Is Nothing Then values = New Dictionary(Of String, String)

                ' Render the schema. ManagedFilePicker fields are
                ' supported via the same fileListProvider pattern
                ' used by Edit Instance \u2014 not currently used by
                ' Factorio's server-settings schema, but supplied
                ' so a future plugin's editor can use file pickers
                ' too. Provider does an instance \u2192 node lookup
                ' and lists the matching directory.
                Dim picker = New Func(Of String, Task(Of IReadOnlyList(Of String)))(
                    AddressOf BuildManagedFileListAsync)

                If _schemaResult IsNot Nothing AndAlso _schemaResult.Panel IsNot Nothing Then
                    _formHost.Controls.Clear()
                End If

                _locked = (Not fileExisted) AndAlso _editor.RequiresExistingFile

                _schemaResult = SchemaFormBuilder.Build(_editor.Schema, values, picker)
                If _schemaResult.Panel IsNot Nothing Then
                    _schemaResult.Panel.Dock = DockStyle.Fill
                    _schemaResult.Panel.Enabled = Not _locked
                    _formHost.Controls.Add(_schemaResult.Panel)
                End If

                If _locked Then
                    SetStatus($"{_editor.RelativePath} hasn't been generated yet. Start the server once to create it, then Reload to edit.",
                              Color.DarkOrange)
                ElseIf fileExisted Then
                    SetStatus($"Loaded {_editor.RelativePath} ({fileText.Length} bytes).", Color.DarkGreen)
                Else
                    SetStatus($"{_editor.RelativePath} doesn't exist yet — schema defaults shown. Save will create the file.",
                              Color.DarkOrange)
                End If
            Finally
                _opInFlight = False
                If Not Me.IsDisposed Then UpdateButtons()
            End Try
        End Function

        Private Sub ReloadClicked()
            ' Confirm if the user might have unsaved edits. We don't
            ' track dirty state precisely \u2014 even an inert refresh
            ' clobbers in-progress typing. Better to ask once than
            ' to lose work silently.
            Dim resp = MessageBox.Show(Me,
                "Reload from disk? Any unsaved changes will be lost.",
                "Reload", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            If resp <> DialogResult.Yes Then Return

            If _disposeCts Is Nothing Then Return
            Dim _unused = LoadAsync(_disposeCts.Token)
        End Sub

        ' ====================================================
        '  Save
        ' ====================================================

        Private Async Sub SaveClicked()
            If _opInFlight Then Return
            If _schemaResult Is Nothing OrElse _schemaResult.ValueExtractor Is Nothing Then
                SetStatus("Form isn't ready.", Color.Firebrick)
                Return
            End If

            Dim provider = ResolveProvider()
            Dim resolved = ResolveNodeContext()
            If provider Is Nothing OrElse resolved Is Nothing Then
                SetStatus("Could not resolve plugin or node for this instance.", Color.Firebrick)
                Return
            End If

            Dim values = _schemaResult.ValueExtractor.Invoke()
            If values Is Nothing Then values = New Dictionary(Of String, String)

            ' Plugin builds the new file text from form values +
            ' the cached existing text. Throwing here is a plugin-
            ' validated rejection \u2014 surface the message and don't
            ' upload.
            Dim newText As String
            Try
                newText = provider.WriteValuesToFile(_editor.Key, values, _lastDownloadedText)
            Catch ex As Exception
                SetStatus($"Validation failed: {ex.Message}", Color.Firebrick)
                Return
            End Try
            If String.IsNullOrEmpty(newText) Then
                SetStatus("Plugin produced empty file content — not saved.", Color.Firebrick)
                Return
            End If

            _opInFlight = True
            UpdateButtons()
            SetStatus("Saving...", Color.DarkOrange)

            Dim token = If(_disposeCts IsNot Nothing, _disposeCts.Token, CancellationToken.None)

            Try
                Await UploadTextAsync(resolved, newText, token)
                If token.IsCancellationRequested OrElse Me.IsDisposed Then Return

                ' Cache the new text so a follow-up Save without an
                ' intervening Reload still has the up-to-date
                ' "existing" content for the round-trip.
                _lastDownloadedText = newText
                SetStatus($"Saved {_editor.RelativePath} ({newText.Length} bytes).", Color.DarkGreen)
            Catch ex As OperationCanceledException
                ' Disposed mid-save \u2014 nothing to render.
            Catch ex As Exception
                If Not Me.IsDisposed Then
                    SetStatus($"Save failed: {ex.Message}", Color.Firebrick)
                End If
            Finally
                _opInFlight = False
                If Not Me.IsDisposed Then UpdateButtons()
            End Try
        End Sub

        ' ====================================================
        '  Helpers
        ' ====================================================

        Private Sub UpdateButtons()
            If Me.IsDisposed Then Return
            Dim ready = (Not _opInFlight) AndAlso (_schemaResult IsNot Nothing)
            _saveButton.Enabled = ready AndAlso Not _locked
            _reloadButton.Enabled = ready
        End Sub

        Private Sub SetStatus(text As String, color As Color)
            If Me.IsDisposed OrElse _statusLabel Is Nothing Then Return
            _statusLabel.Text = text
            _statusLabel.ForeColor = color
        End Sub

        ''' <summary>
        ''' Detect whether a NodeApiException is a 404. Now that
        ''' WrapException populates the wrapper's StatusCode
        ''' directly we just check the property; the legacy fallback
        ''' that sniffs the inner HttpRequestException is kept for
        ''' defence in case a future code path constructs a
        ''' NodeApiException without a StatusCode.
        ''' </summary>
        Private Shared Function IsNotFound(ex As NodeApiException) As Boolean
            If ex Is Nothing Then Return False
            If ex.StatusCode.HasValue AndAlso
               ex.StatusCode.Value = HttpStatusCode.NotFound Then
                Return True
            End If
            Dim http = TryCast(ex.InnerException, HttpRequestException)
            If http Is Nothing Then Return False
            Return http.StatusCode.HasValue AndAlso
                   http.StatusCode.Value = HttpStatusCode.NotFound
        End Function

        ''' <summary>
        ''' Bundle: node client + install path. Resolved once per
        ''' Load/Save call. Re-resolved each time so a node
        ''' addr/token edit takes effect on the next op without
        ''' panel rebuild.
        ''' </summary>
        Private Class ResolvedNodeContext
            Public Client As INodeClient
            Public InstallPath As String
        End Class

        Private Function ResolveNodeContext() As ResolvedNodeContext
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
                    Return New ResolvedNodeContext With {
                        .Client = client,
                        .InstallPath = installEntity.InstallPath
                    }
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Private Function ResolveProvider() As IInstanceFileEditorProvider
            Try
                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return Nothing
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then Return Nothing
                    Dim plugin = registry.GetPlugin(instanceEntity.GameId)
                    Return TryCast(plugin, IInstanceFileEditorProvider)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Auto-derived allowedRoots for the editor's RelativePath.
        ''' For files at the install root (e.g. "server-settings.json")
        ''' the root is the filename itself \u2014 the file endpoint's
        ''' Equals check matches just that one file. For files under
        ''' a subdirectory (e.g. "config/world.json") the root is
        ''' the parent dir \u2014 the StartsWith check matches anything
        ''' under it (constrained further by the extension filter).
        ''' </summary>
        Private Function DerivedAllowedRoots() As IReadOnlyList(Of String)
            Dim rel = If(_editor.RelativePath, "").Replace("\"c, "/"c)
            Dim slashIdx = rel.LastIndexOf("/"c)
            If slashIdx < 0 Then
                ' File at install root \u2014 use the filename itself as
                ' the root. The equality check in FileEndpoints
                ' validates the exact path, and ".bak" / other
                ' extensions on similar names are rejected by the
                ' extension allowlist below.
                Return New String() {rel}
            End If
            Return New String() {rel.Substring(0, slashIdx)}
        End Function

        Private Function DerivedAllowedExtensions() As IReadOnlyList(Of String)
            Dim ext = Path.GetExtension(_editor.RelativePath)
            If String.IsNullOrEmpty(ext) Then Return New List(Of String)
            Return New String() {ext}
        End Function

        ''' <summary>
        ''' Download the editor's file as text. INodeClient streams
        ''' into a caller-provided destination; we use a MemoryStream
        ''' so we can pull the bytes back out as UTF-8. Config files
        ''' are small (kilobytes), so the in-memory copy is fine.
        ''' </summary>
        Private Async Function DownloadAsTextAsync(resolved As ResolvedNodeContext,
                                                    token As CancellationToken) As Task(Of String)
            Using ms As New MemoryStream()
                Await resolved.Client.DownloadFileAsync(
                    _instanceId,
                    resolved.InstallPath,
                    _editor.RelativePath,
                    DerivedAllowedRoots(),
                    DerivedAllowedExtensions(),
                    ms,
                    token)
                ' UTF-8 with no BOM \u2014 matches what plugin will
                ' produce on save and what Factorio writes natively.
                ' Errant BOMs in user-edited files round-trip via
                ' the plugin's text-preservation path so we don't
                ' need to strip them here.
                Return Encoding.UTF8.GetString(ms.ToArray())
            End Using
        End Function

        ''' <summary>
        ''' Upload the supplied text as the file's new content.
        ''' UTF-8 with no BOM, matching DownloadAsTextAsync. The
        ''' upload endpoint's overwrite flag is true \u2014 we always
        ''' want Save to replace whatever's there, never to fail
        ''' on existing-file conflicts.
        ''' </summary>
        Private Async Function UploadTextAsync(resolved As ResolvedNodeContext,
                                                content As String,
                                                token As CancellationToken) As Task
            Dim bytes = Encoding.UTF8.GetBytes(If(content, ""))
            Using ms As New MemoryStream(bytes, writable:=False)
                Await resolved.Client.UploadFileAsync(
                    _instanceId,
                    resolved.InstallPath,
                    _editor.RelativePath,
                    DerivedAllowedRoots(),
                    DerivedAllowedExtensions(),
                    ms,
                    overwrite:=True,
                    cancellation:=token)
            End Using
        End Function

        ''' <summary>
        ''' File-list provider passed to SchemaFormBuilder for any
        ''' ManagedFilePicker fields in the editor's schema. Mirrors
        ''' EditInstanceForm's BuildSavesProviderForCurrentInstance \u2014
        ''' looks up the plugin's IManagedDirectoriesProvider, finds
        ''' the matching directory by RelativePath, lists files via
        ''' the node, returns basenames sorted newest-first.
        ''' Returns an empty list on any failure so the combo's
        ''' free-text path stays usable.
        '''
        ''' Factorio's current server-settings schema doesn't use
        ''' ManagedFilePicker fields \u2014 this helper exists for
        ''' future plugins whose editors might want to point at a
        ''' file under a managed directory.
        ''' </summary>
        Private Async Function BuildManagedFileListAsync(dirRef As String) As Task(Of IReadOnlyList(Of String))
            If String.IsNullOrEmpty(dirRef) Then Return New List(Of String)
            Try
                Dim resolved = ResolveNodeContext()
                If resolved Is Nothing Then Return New List(Of String)

                Dim registry = ManagerProgram.Services.GetService(Of PluginRegistry)()
                If registry Is Nothing Then Return New List(Of String)

                Dim gameId As String = Nothing
                Using scope = ManagerProgram.Services.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim instanceEntity = db.Instances.Find(_instanceId)
                    If instanceEntity Is Nothing Then Return New List(Of String)
                    gameId = instanceEntity.GameId
                End Using

                Dim plugin = registry.GetPlugin(gameId)
                Dim dirProvider = TryCast(plugin, IManagedDirectoriesProvider)
                If dirProvider Is Nothing Then Return New List(Of String)

                Dim minimalConfig As New InstanceConfig With {
                    .InstanceId = _instanceId,
                    .GameId = gameId
                }
                Dim dirs = dirProvider.GetManagedDirectories(minimalConfig)
                If dirs Is Nothing Then Return New List(Of String)

                Dim resolvedRel As String = dirRef
                Dim allowedExtensions As IReadOnlyList(Of String) = Nothing
                For Each d In dirs
                    If d Is Nothing Then Continue For
                    If String.Equals(d.RelativePath, dirRef, StringComparison.OrdinalIgnoreCase) Then
                        resolvedRel = If(d.RelativePath, dirRef).Replace("{InstanceId}", _instanceId)
                        allowedExtensions = d.AllowedExtensions
                        Exit For
                    End If
                Next

                Dim entries = Await resolved.Client.ListFilesAsync(
                    _instanceId,
                    resolved.InstallPath,
                    resolvedRel,
                    New String() {resolvedRel},
                    allowedExtensions,
                    CancellationToken.None)

                If entries Is Nothing Then Return New List(Of String)
                Return entries.
                    OrderByDescending(Function(f) f.ModifiedUtc).
                    Select(Function(f) ShortName(f.RelativePath)).
                    Where(Function(n) Not String.IsNullOrEmpty(n)).
                    ToList()
            Catch
                Return New List(Of String)
            End Try
        End Function

        Private Shared Function ShortName(relativePath As String) As String
            If String.IsNullOrEmpty(relativePath) Then Return ""
            Dim slashIdx = relativePath.LastIndexOfAny(New Char() {"/"c, "\"c})
            If slashIdx < 0 Then Return relativePath
            Return relativePath.Substring(slashIdx + 1)
        End Function

    End Class

End Namespace
