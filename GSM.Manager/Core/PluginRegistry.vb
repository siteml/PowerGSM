Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Runtime.Loader
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.Extensions.Logging
Imports Basic.Reference.Assemblies
Imports GSM.Plugin

' ============================================================
'  PluginRegistry — Roslyn compilation + hot-reload
'
'  Compiles .vb plugin source files from the Plugins directory
'  using the Roslyn VB.Net compiler. Each reload cycle creates
'  a new AssemblyLoadContext, compiles all plugins into it,
'  and unloads the previous context.
'
'  Instances never hold a direct plugin reference. They store
'  a GameId and resolve through GetPlugin(gameId) at the
'  moment they need it. This is what makes hot-reload safe.
'
'  Reload is manual-only (triggered from UI or remote command).
' ============================================================

Namespace GSM.Manager.Core

    ' ============================================================
    '  Coordinator interfaces used by other services
    ' ============================================================

    ''' <summary>
    ''' Coordinates log parsers across instances.
    ''' InstanceManager creates one parser per instance.
    ''' </summary>
    Public Interface ILogParserCoordinator
        Function CreateParser(gameId As String) As ILogParser
    End Interface

    ''' <summary>
    ''' Abstracts the manager-side ring buffer store so services
    ''' don't depend on the concrete ManagerRingBufferStore class.
    ''' </summary>
    Public Interface IRingBufferStore
        Sub Append(instanceId As String, line As LogLine)
        Function GetTail(instanceId As String, count As Integer) As IReadOnlyList(Of LogLine)
        Sub RemoveBuffer(instanceId As String)
    End Interface

    ' ============================================================
    '  PluginRegistry
    ' ============================================================

    Public Class PluginRegistry
        Implements ILogParserCoordinator

        Private ReadOnly _plugins As New ConcurrentDictionary(Of String, IGamePlugin)
        Private ReadOnly _pluginStatuses As New ConcurrentDictionary(Of String, PluginLoadStatus)
        Private ReadOnly _logger As ILogger(Of PluginRegistry)
        Private ReadOnly _pluginsDirectory As String
        Private _loadContext As AssemblyLoadContext
        Private ReadOnly _lockObj As New Object()

        Public Sub New(logger As ILogger(Of PluginRegistry),
                       Optional pluginsDirectory As String = Nothing)
            _logger = logger
            _pluginsDirectory = If(pluginsDirectory,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"))
        End Sub

        ''' <summary>
        ''' Returns a plugin by GameId, or Nothing if not loaded.
        ''' </summary>
        Public Function GetPlugin(gameId As String) As IGamePlugin
            Dim result As IGamePlugin = Nothing
            _plugins.TryGetValue(gameId, result)
            Return result
        End Function

        ''' <summary>
        ''' Returns all currently loaded plugins.
        ''' </summary>
        Public Function GetAllPlugins() As IReadOnlyList(Of IGamePlugin)
            Return _plugins.Values.ToList()
        End Function

        ''' <summary>
        ''' Returns the load status for a given source file.
        ''' </summary>
        Public Function GetStatus(fileName As String) As PluginLoadStatus
            Dim result As PluginLoadStatus = PluginLoadStatus.Unloaded
            _pluginStatuses.TryGetValue(fileName, result)
            Return result
        End Function

        ''' <summary>
        ''' Returns all loaded GameIds.
        ''' </summary>
        Public Function GetLoadedGameIds() As IReadOnlyList(Of String)
            Return _plugins.Keys.ToList()
        End Function

        ' ============================================================
        '  Load / Reload
        ' ============================================================

        ''' <summary>
        ''' Loads or reloads all plugins from the Plugins directory.
        ''' Returns a summary of what changed.
        ''' </summary>
        Public Function ReloadAll(Optional orphanDetector As IOrphanDetector = Nothing) As PluginReloadSummary
            SyncLock _lockObj
                Return ReloadAllInternal(orphanDetector)
            End SyncLock
        End Function

        Private Function ReloadAllInternal(orphanDetector As IOrphanDetector) As PluginReloadSummary
            Dim summary As New PluginReloadSummary With {
                .LoadedPlugins = New List(Of String),
                .AddedGameIds = New List(Of String),
                .RemovedGameIds = New List(Of String),
                .UpdatedGameIds = New List(Of String),
                .CompilationErrors = New List(Of PluginCompilationError),
                .OrphanedInstallationIds = New List(Of String),
                .OrphanedInstanceIds = New List(Of String)
            }

            ' Remember previous game IDs for diff
            Dim previousGameIds = _plugins.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)

            ' Unload previous context
            If _loadContext IsNot Nothing Then
                _loadContext.Unload()
                _loadContext = Nothing
            End If
            _plugins.Clear()
            _pluginStatuses.Clear()

            ' Ensure plugins directory exists
            If Not Directory.Exists(_pluginsDirectory) Then
                Directory.CreateDirectory(_pluginsDirectory)
                _logger.LogInformation("Created plugins directory: {Dir}", _pluginsDirectory)
                Return summary
            End If

            ' Find all .vb files
            Dim sourceFiles = Directory.GetFiles(_pluginsDirectory, "*.vb",
                                                  SearchOption.TopDirectoryOnly)
            If sourceFiles.Length = 0 Then
                _logger.LogInformation("No plugin source files found in {Dir}", _pluginsDirectory)
                ' Detect orphans from previously loaded plugins
                DetectOrphans(previousGameIds, orphanDetector, summary)
                Return summary
            End If

            ' Create new load context shared by all plugin assemblies —
            ' that way they all get unloaded together on the next reload.
            _loadContext = New AssemblyLoadContext("PluginContext", isCollectible:=True)

            ' Gather references once (shared across per-file compilations)
            Dim references = GetMetadataReferences()

            Dim compilationOptions As New VisualBasicCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optionStrict:=OptionStrict.Off,
                optionExplicit:=True,
                optionInfer:=True)

            ' Compile each plugin file independently so one failure doesn't
            ' prevent others from loading. Each file gets its own assembly.
            For Each filePath In sourceFiles
                Dim fileName = Path.GetFileName(filePath)
                Dim asmName = "GSM.Plugins." & Path.GetFileNameWithoutExtension(filePath)

                Dim tree As SyntaxTree
                Try
                    Dim sourceText = File.ReadAllText(filePath)
                    tree = VisualBasicSyntaxTree.ParseText(sourceText, path:=filePath)
                Catch ex As Exception
                    summary.CompilationErrors.Add(New PluginCompilationError With {
                        .FileName = fileName,
                        .Message = $"Failed to read file: {ex.Message}"
                    })
                    Continue For
                End Try

                Dim compilation = VisualBasicCompilation.Create(
                    asmName,
                    {tree},
                    references,
                    compilationOptions)

                Using ms As New MemoryStream()
                    Dim emitResult = compilation.Emit(ms)

                    If Not emitResult.Success Then
                        For Each diag In emitResult.Diagnostics.
                                Where(Function(d) d.Severity = DiagnosticSeverity.Error)
                            Dim loc = diag.Location
                            Dim lineSpan = loc.GetLineSpan()
                            summary.CompilationErrors.Add(New PluginCompilationError With {
                                .FileName = If(lineSpan.Path, fileName),
                                .Line = lineSpan.StartLinePosition.Line + 1,
                                .Column = lineSpan.StartLinePosition.Character + 1,
                                .ErrorCode = diag.Id,
                                .Message = diag.GetMessage()
                            })
                        Next
                        _logger.LogError("Plugin {File} failed to compile with {Count} error(s)",
                                         fileName,
                                         emitResult.Diagnostics.Count(Function(d) d.Severity = DiagnosticSeverity.Error))
                        Continue For
                    End If

                    ' Load this plugin's assembly
                    ms.Seek(0, SeekOrigin.Begin)
                    Dim pluginAssembly As Assembly
                    Try
                        pluginAssembly = _loadContext.LoadFromStream(ms)
                    Catch ex As Exception
                        summary.CompilationErrors.Add(New PluginCompilationError With {
                            .FileName = fileName,
                            .Message = $"Failed to load assembly: {ex.Message}"
                        })
                        Continue For
                    End Try

                    ' Find and instantiate IGamePlugin implementations
                    For Each pluginType In pluginAssembly.GetTypes().
                            Where(Function(t) Not t.IsAbstract AndAlso
                                              Not t.IsInterface AndAlso
                                              GetType(IGamePlugin).IsAssignableFrom(t))
                        Try
                            Dim instance = DirectCast(Activator.CreateInstance(pluginType), IGamePlugin)
                            Dim gid = instance.GameId

                            If _plugins.ContainsKey(gid) Then
                                summary.CompilationErrors.Add(New PluginCompilationError With {
                                    .FileName = pluginType.Name,
                                    .Message = $"Duplicate GameId '{gid}' — skipping"
                                })
                                _pluginStatuses(pluginType.Name) = PluginLoadStatus.DuplicateGameId
                                Continue For
                            End If

                            _plugins(gid) = instance
                            _pluginStatuses(pluginType.Name) = PluginLoadStatus.Loaded
                            summary.LoadedPlugins.Add(gid)

                            If previousGameIds.Contains(gid) Then
                                summary.UpdatedGameIds.Add(gid)
                            Else
                                summary.AddedGameIds.Add(gid)
                            End If

                            _logger.LogInformation("Loaded plugin: {GameId} ({Type})",
                                                   gid, pluginType.Name)
                        Catch ex As Exception
                            summary.CompilationErrors.Add(New PluginCompilationError With {
                                .FileName = pluginType.Name,
                                .Message = $"Failed to instantiate: {ex.Message}"
                            })
                            _pluginStatuses(pluginType.Name) = PluginLoadStatus.InterfaceMismatch
                        End Try
                    Next
                End Using
            Next

            ' Detect removed plugins
            Dim currentGameIds = _plugins.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            For Each oldId In previousGameIds
                If Not currentGameIds.Contains(oldId) Then
                    summary.RemovedGameIds.Add(oldId)
                End If
            Next

            DetectOrphans(summary.RemovedGameIds, orphanDetector, summary)

            _logger.LogInformation(
                "Plugin reload complete: {Loaded} loaded, {Added} added, {Removed} removed, {Errors} errors",
                summary.LoadedPlugins.Count, summary.AddedGameIds.Count,
                summary.RemovedGameIds.Count, summary.CompilationErrors.Count)

            Return summary
        End Function

        ' ============================================================
        '  ILogParserCoordinator
        ' ============================================================

        Public Function CreateParser(gameId As String) As ILogParser Implements ILogParserCoordinator.CreateParser
            Dim plugin = GetPlugin(gameId)
            If plugin Is Nothing Then Return Nothing
            Return plugin.CreateLogParser()
        End Function

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Sub DetectOrphans(removedGameIds As IEnumerable(Of String),
                                  orphanDetector As IOrphanDetector,
                                  summary As PluginReloadSummary)
            If orphanDetector Is Nothing Then Return
            For Each gid In removedGameIds
                Dim orphanedInstalls = orphanDetector.GetOrphanedInstallationIds(gid)
                summary.OrphanedInstallationIds.AddRange(orphanedInstalls)
                Dim orphanedInstances = orphanDetector.GetOrphanedInstanceIds(gid)
                summary.OrphanedInstanceIds.AddRange(orphanedInstances)
            Next
        End Sub

        ''' <summary>
        ''' Returns a MetadataReference for GSM.Contracts so the Roslyn
        ''' compiler can resolve IGamePlugin / ILogParser / etc. when
        ''' compiling plugins.
        '''
        ''' Relies on GSM.Contracts.dll being a loose file next to
        ''' GSM.Manager.exe. This is enforced by
        ''' &lt;ExcludeFromSingleFile&gt;true&lt;/ExcludeFromSingleFile&gt; on the
        ''' Contracts ProjectReference in GSM.Manager.vbproj. Without
        ''' that, Assembly.Location returns an empty string in single-file
        ''' publish and there's no clean way to feed the bundled assembly
        ''' to Roslyn.
        ''' </summary>
        Private Shared _contractsReference As MetadataReference
        Private Shared ReadOnly _contractsRefLock As New Object()

        Private Shared Function GetContractsReference() As MetadataReference
            If _contractsReference IsNot Nothing Then Return _contractsReference
            SyncLock _contractsRefLock
                If _contractsReference IsNot Nothing Then Return _contractsReference

                Dim contractsAsm = GetType(IGamePlugin).Assembly
                Dim asmPath = contractsAsm.Location

                If Not String.IsNullOrEmpty(asmPath) AndAlso File.Exists(asmPath) Then
                    _contractsReference = MetadataReference.CreateFromFile(asmPath)
                    Return _contractsReference
                End If

                ' Defensive fallback: the loose DLL next to the executable.
                ' Cheap to check and covers any edge case where
                ' Assembly.Location is empty.
                Dim sideBySide = Path.Combine(AppContext.BaseDirectory, "GSM.Contracts.dll")
                If File.Exists(sideBySide) Then
                    _contractsReference = MetadataReference.CreateFromFile(sideBySide)
                    Return _contractsReference
                End If

                Throw New InvalidOperationException(
                    "Could not locate GSM.Contracts.dll for Roslyn compilation. " &
                    "GSM.Manager.vbproj must mark the GSM.Contracts ProjectReference " &
                    "with <ExcludeFromSingleFile>true</ExcludeFromSingleFile> so the " &
                    "DLL is published as a loose file next to GSM.Manager.exe.")
            End SyncLock
        End Function

        Private Shared Function GetMetadataReferences() As List(Of MetadataReference)
            Dim refs As New List(Of MetadataReference)

            ' .NET 8 framework references — sourced from the
            ' Basic.Reference.Assemblies NuGet package, which embeds the
            ' reference assemblies as resources. This is deployment-shape
            ' independent: it works in dev builds, framework-dependent
            ' publish, self-contained publish, single-file, and trimmed
            ' publishes alike.
            '
            ' We deliberately do NOT use TRUSTED_PLATFORM_ASSEMBLIES here.
            ' That works in dev and in non-single-file publishes, but in
            ' .NET 6+ self-contained single-file publishes (which is what
            ' GSM.Manager uses), TPA entries point to virtual paths inside
            ' the bundle that don't exist on disk. CreateFromFile throws
            ' FileNotFoundException for every entry, refs ends up empty,
            ' and every plugin compiles against zero BCL types — which
            ' surfaces as a flood of "System.String / System.Void / etc.
            ' is not defined" errors in the published build only.
            refs.AddRange(ReferenceAssemblies.Net80)

            ' GSM.Contracts — handled separately because it isn't a
            ' framework assembly and isn't covered by
            ' Basic.Reference.Assemblies.
            Try
                refs.Add(GetContractsReference())
            Catch ex As Exception
                ' Re-throw with context; without Contracts every plugin will
                ' fail with cascading "type not defined" errors and the user
                ' won't know why.
                Throw New InvalidOperationException(
                    "Failed to add GSM.Contracts to plugin compilation references. " & ex.Message,
                    ex)
            End Try

            Return refs
        End Function

    End Class

End Namespace