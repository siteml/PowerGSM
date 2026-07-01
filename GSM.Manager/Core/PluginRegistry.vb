Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Runtime.Loader
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.Extensions.Logging
Imports Basic.Reference.Assemblies
Imports GSM.Node.Api
Imports GSM.Plugin
Imports GSM.Utility

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

        ''' <summary>
        ''' Phase 7-2 — raised at the end of every successful
        ''' ReloadAll (including the startup load), AFTER the new
        ''' plugin set is in place. Fired from inside the reload lock
        ''' on the caller's thread — handlers must offload real work
        ''' (UtilityPluginHost restarts its plugins via Task.Run).
        ''' </summary>
        Public Event Reloaded As EventHandler

        Private ReadOnly _plugins As New ConcurrentDictionary(Of String, IGamePlugin)
        Private ReadOnly _pluginStatuses As New ConcurrentDictionary(Of String, PluginLoadStatus)

        ' Phase 7-1 — utility plugins (second plugin kind), keyed by
        ' PluginId. Same lifecycle as game plugins: discovered per
        ' file, hot-reloaded with the shared ALC, cleared on reload.
        ' Manifests and declared contracts versions for utility
        ' plugins live in the same per-id maps as game plugins — the
        ' two id spaces are kept collision-free at load time.
        Private ReadOnly _utilityPlugins As New ConcurrentDictionary(Of String, IUtilityPlugin)

        ' Phase 5f-3 — declared contracts version per loaded
        ' plugin, keyed by GameId. Populated when a plugin loads
        ' successfully (its `' <RequiresContracts: N>' magic
        ' comment was parsed and either matched the running
        ' version exactly or was older). Cleared on every reload
        ' alongside _plugins. Used by the Plugin Status form to
        ' show the declared version next to each loaded plugin
        ' so users can spot "Loaded but old" cases at a glance.
        Private ReadOnly _declaredContractsByGameId As New ConcurrentDictionary(Of String, Integer)

        ' Phase 6-1 — parsed inline manifest per loaded GameId
        ' (id/name/version/author/dependencies). Cleared on every reload
        ' alongside _plugins. Drives the Plugin Status version/author/
        ' source columns and 6-2+ update-tracking by version.
        Private ReadOnly _manifestsByGameId As New ConcurrentDictionary(Of String, PluginManifest)

        Private ReadOnly _logger As ILogger(Of PluginRegistry)
        Private ReadOnly _pluginsDirectory As String
        Private _loadContext As AssemblyLoadContext
        Private ReadOnly _lockObj As New Object()

        ' Phase 5f-3 — magic-comment regex. Matches at any line
        ' position, captures the version integer. Whitespace
        ' tolerant (`'  <RequiresContracts:  42>` is fine), but
        ' case-sensitive on the keyword to keep things
        ' predictable. Compiled once because ReloadAll runs once
        ' per plugin source on every reload.
        Private Shared ReadOnly s_RequiresContractsRegex As _
            New Regex("'\s*<RequiresContracts\s*:\s*(\d+)\s*>",
                      RegexOptions.Compiled)

        Public Sub New(logger As ILogger(Of PluginRegistry),
                       Optional pluginsDirectory As String = Nothing)
            _logger = logger
            _pluginsDirectory = If(pluginsDirectory,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"))
        End Sub

        ''' <summary>
        ''' The directory plugin .vb sources are loaded from. Exposed so
        ''' the compatibility checker (Phase 5l-3) compiles exactly the
        ''' files this registry loads.
        ''' </summary>
        Public ReadOnly Property PluginsDirectory As String
            Get
                Return _pluginsDirectory
            End Get
        End Property

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
        ''' Phase 7-1 — returns a utility plugin by PluginId, or
        ''' Nothing if not loaded.
        ''' </summary>
        Public Function GetUtilityPlugin(pluginId As String) As IUtilityPlugin
            Dim result As IUtilityPlugin = Nothing
            _utilityPlugins.TryGetValue(pluginId, result)
            Return result
        End Function

        ''' <summary>
        ''' Phase 7-1 — returns all currently loaded utility plugins.
        ''' </summary>
        Public Function GetUtilityPlugins() As IReadOnlyList(Of IUtilityPlugin)
            Return _utilityPlugins.Values.ToList()
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

        ''' <summary>
        ''' Phase 5f-3 — returns the contracts version a loaded
        ''' plugin declared via its `' &lt;RequiresContracts: N&gt;'
        ''' magic comment, or Nothing if the plugin isn't loaded
        ''' or didn't declare a version. Used by the Plugin Status
        ''' form to render the per-plugin Contracts column.
        ''' </summary>
        Public Function GetDeclaredContractsVersion(gameId As String) As Integer?
            If String.IsNullOrEmpty(gameId) Then Return Nothing
            Dim result As Integer
            If _declaredContractsByGameId.TryGetValue(gameId, result) Then Return result
            Return Nothing
        End Function

        ''' <summary>
        ''' Phase 6-1 — the parsed inline manifest for a loaded plugin,
        ''' or Nothing if the plugin isn't loaded. A legacy/local plugin
        ''' returns a manifest with HasPluginBlock = False.
        ''' </summary>
        Public Function GetManifest(gameId As String) As PluginManifest
            If String.IsNullOrEmpty(gameId) Then Return Nothing
            Dim result As PluginManifest = Nothing
            _manifestsByGameId.TryGetValue(gameId, result)
            Return result
        End Function

        ''' <summary>
        ''' Parse the `' &lt;RequiresContracts: N&gt;' magic comment
        ''' from a plugin source file's text. Returns the captured
        ''' integer when found, Nothing when absent. Multiple
        ''' matches return the FIRST occurrence — plugins should
        ''' put the comment near the top, but we don't enforce
        ''' position so an unusually-structured plugin still works.
        '''
        ''' Cheap enough to call before invoking Roslyn so we can
        ''' fail fast on a too-new declaration without paying the
        ''' compile cost. Pulled out as a private helper rather
        ''' than inlined so the parsing convention has one place
        ''' to live, in case the format ever evolves (e.g. adding
        ''' a min/max range).
        ''' </summary>
        Private Shared Function ParseRequiresContractsVersion(sourceText As String) As Integer?
            If String.IsNullOrEmpty(sourceText) Then Return Nothing
            Dim match = s_RequiresContractsRegex.Match(sourceText)
            If Not match.Success Then Return Nothing
            Dim parsed As Integer
            If Integer.TryParse(match.Groups(1).Value, parsed) Then Return parsed
            Return Nothing
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
            _utilityPlugins.Clear()
            _declaredContractsByGameId.Clear()
            _manifestsByGameId.Clear()

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

            ' Gather references once (shared across per-file compilations).
            ' Plugins that declare capabilities but NOT network get a
            ' network-stripped variant (7-3b) — computed lazily per file
            ' below so the common case pays nothing.
            Dim references = GetMetadataReferences()
            Dim networkStrippedReferences As List(Of MetadataReference) = Nothing

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

                ' Read the source first — needed both for the magic
                ' comment parse and for Roslyn. One read, used twice.
                Dim sourceText As String
                Try
                    sourceText = File.ReadAllText(filePath)
                Catch ex As Exception
                    summary.CompilationErrors.Add(New PluginCompilationError With {
                        .FileName = fileName,
                        .Message = $"Failed to read file: {ex.Message}"
                    })
                    Continue For
                End Try

                ' Phase 6-1 — parse the inline manifest (id/name/version/
                ' author/deps). Additive: the contracts-version
                ' negotiation below keeps its own legacy parse for now.
                Dim manifest = PluginManifestParser.Parse(sourceText)

                ' Phase 7-3b — reference-set gating. A plugin that
                ' declares capabilities (the `requires` attribute) but
                ' NOT `network` compiles WITHOUT the System.Net.*
                ' reference assemblies, so HttpClient/Socket/etc become
                ' a compile error naming the missing capability instead
                ' of silently-permitted runtime behaviour. This applies
                ' ONLY to plugins that opted into the capability model:
                ' game plugins (no `requires`) keep every reference, so
                ' their legitimate network use is untouched. It's a
                ' genuine compile-time gate for honest and lazy code;
                ' a determined author can't be stopped by it (see
                ' Phase7_Plan.md scoping note).
                Dim fileReferences = references
                Dim declaresCapabilities = manifest IsNot Nothing AndAlso
                                           manifest.Requires IsNot Nothing AndAlso
                                           manifest.Requires.Count > 0
                If declaresCapabilities AndAlso
                   Not manifest.Requires.Contains(UtilityCapabilities.Network) Then
                    If networkStrippedReferences Is Nothing Then
                        networkStrippedReferences = StripNetworkReferences(references)
                    End If
                    fileReferences = networkStrippedReferences
                End If

                ' Phase 5f-3 — contracts version negotiation.
                ' Three outcomes from comparing the plugin's
                ' declared version against the manager's running
                ' NodeApiContract.ContractsVersion:
                '
                '   plugin == manager : load silently, stamp the
                '                       version on the per-GameId
                '                       map after a successful
                '                       instantiate.
                '   plugin <  manager : load, log a debug note.
                '                       Older contracts are by
                '                       definition still understood
                '                       by the current manager
                '                       (contracts only break on a
                '                       contracts-version bump).
                '   plugin >  manager : skip BEFORE Roslyn so the
                '                       user sees a clear single-
                '                       line error rather than a
                '                       cascade of "type X not
                '                       defined" diagnostics from
                '                       a method we don't have yet.
                '   missing            : log a warning and treat
                '                       as 1 (the only contracts
                '                       version that's ever
                '                       existed, so this is
                '                       harmless until 2 ships).
                Dim runningContractsVersion = NodeApiContract.ContractsVersion
                Dim declaredContractsVersion As Integer? = ParseRequiresContractsVersion(sourceText)
                Dim effectiveDeclaredVersion As Integer
                If declaredContractsVersion.HasValue Then
                    effectiveDeclaredVersion = declaredContractsVersion.Value
                Else
                    effectiveDeclaredVersion = 1
                    _logger.LogWarning(
                        "Plugin {File} has no '<RequiresContracts: N>' magic comment; assuming v1. " &
                        "Add the comment near the top of the file to make this explicit.",
                        fileName)
                End If

                If effectiveDeclaredVersion > runningContractsVersion Then
                    summary.CompilationErrors.Add(New PluginCompilationError With {
                        .FileName = fileName,
                        .Message = $"Plugin requires contracts v{effectiveDeclaredVersion}, " &
                                   $"but this manager runs contracts v{runningContractsVersion}. " &
                                   "Update the manager or use a plugin compiled for v" &
                                   $"{runningContractsVersion}."
                    })
                    _pluginStatuses(fileName) = PluginLoadStatus.ContractsVersionTooNew
                    _logger.LogError(
                        "Plugin {File} requires contracts v{Declared}, manager runs v{Running} — skipped.",
                        fileName, effectiveDeclaredVersion, runningContractsVersion)
                    Continue For
                ElseIf effectiveDeclaredVersion < runningContractsVersion Then
                    _logger.LogDebug(
                        "Plugin {File} targets contracts v{Declared}, manager runs v{Running} — should be compatible.",
                        fileName, effectiveDeclaredVersion, runningContractsVersion)
                End If

                Dim tree As SyntaxTree
                Try
                    tree = VisualBasicSyntaxTree.ParseText(sourceText, path:=filePath)
                Catch ex As Exception
                    summary.CompilationErrors.Add(New PluginCompilationError With {
                        .FileName = fileName,
                        .Message = $"Failed to parse: {ex.Message}"
                    })
                    Continue For
                End Try

                Dim compilation = VisualBasicCompilation.Create(
                    asmName,
                    {tree},
                    fileReferences,
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

                            ' Phase 5f-3 — stamp the declared
                            ' contracts version on the per-GameId
                            ' map. Done here (after a successful
                            ' instantiate) rather than at parse
                            ' time so the entry only exists for
                            ' plugins that actually loaded —
                            ' avoids leaking entries for files
                            ' that compiled but failed to
                            ' instantiate, or were rejected as
                            ' DuplicateGameId.
                            _declaredContractsByGameId(gid) = effectiveDeclaredVersion
                            _manifestsByGameId(gid) = manifest

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

                    ' Phase 7-1 — find and instantiate IUtilityPlugin
                    ' implementations. Two extra rules vs game plugins
                    ' (Phase 7 Decision 3): a <plugin> manifest with id
                    ' AND version is REQUIRED (utility plugins are new —
                    ' no legacy leniency), and the runtime PluginId must
                    ' match the manifest id. Ids share one keyspace with
                    ' game plugins so cross-kind collisions are refused.
                    For Each utilityType In pluginAssembly.GetTypes().
                            Where(Function(t) Not t.IsAbstract AndAlso
                                              Not t.IsInterface AndAlso
                                              GetType(IUtilityPlugin).IsAssignableFrom(t))
                        Try
                            If manifest Is Nothing OrElse Not manifest.HasPluginBlock OrElse
                               String.IsNullOrEmpty(manifest.Id) OrElse
                               String.IsNullOrEmpty(manifest.Version) Then
                                summary.CompilationErrors.Add(New PluginCompilationError With {
                                    .FileName = fileName,
                                    .Message = "Utility plugins require a ' <plugin id=""..."" version=""...""> manifest — skipped."
                                })
                                _pluginStatuses(utilityType.Name) = PluginLoadStatus.InterfaceMismatch
                                Continue For
                            End If

                            Dim utilityInstance = DirectCast(Activator.CreateInstance(utilityType), IUtilityPlugin)
                            Dim pid = utilityInstance.PluginId

                            If String.IsNullOrEmpty(pid) OrElse
                               Not String.Equals(pid, manifest.Id, StringComparison.OrdinalIgnoreCase) Then
                                summary.CompilationErrors.Add(New PluginCompilationError With {
                                    .FileName = fileName,
                                    .Message = $"Utility plugin's PluginId '{pid}' doesn't match the manifest id '{manifest.Id}' — skipped."
                                })
                                _pluginStatuses(utilityType.Name) = PluginLoadStatus.InterfaceMismatch
                                Continue For
                            End If

                            If _utilityPlugins.ContainsKey(pid) OrElse _plugins.ContainsKey(pid) Then
                                summary.CompilationErrors.Add(New PluginCompilationError With {
                                    .FileName = utilityType.Name,
                                    .Message = $"Duplicate plugin id '{pid}' — skipping"
                                })
                                _pluginStatuses(utilityType.Name) = PluginLoadStatus.DuplicateGameId
                                Continue For
                            End If

                            ' Phase 7-3 — unknown capability names are a
                            ' WARNING (forward-compat with future
                            ' capability sets), never a load error.
                            If manifest.Requires IsNot Nothing Then
                                For Each capName In manifest.Requires
                                    If Not UtilityCapabilities.IsKnown(capName) Then
                                        _logger.LogWarning(
                                            "Utility plugin {Id} declares unknown capability '{Cap}' — ignored (this manager's contracts v{Version} doesn't know it)",
                                            pid, capName, NodeApiContract.ContractsVersion)
                                    End If
                                Next
                            End If

                            _utilityPlugins(pid) = utilityInstance
                            _pluginStatuses(utilityType.Name) = PluginLoadStatus.Loaded
                            summary.LoadedPlugins.Add(pid)
                            _declaredContractsByGameId(pid) = effectiveDeclaredVersion
                            _manifestsByGameId(pid) = manifest

                            _logger.LogInformation("Loaded utility plugin: {PluginId} ({Type})",
                                                   pid, utilityType.Name)
                        Catch ex As Exception
                            summary.CompilationErrors.Add(New PluginCompilationError With {
                                .FileName = utilityType.Name,
                                .Message = $"Failed to instantiate utility plugin: {ex.Message}"
                            })
                            _pluginStatuses(utilityType.Name) = PluginLoadStatus.InterfaceMismatch
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

            ' Phase 7-2 — let subscribers (UtilityPluginHost) react to
            ' the new plugin set. Never let a handler break the reload.
            Try
                RaiseEvent Reloaded(Me, EventArgs.Empty)
            Catch ex As Exception
                _logger.LogWarning(ex, "A Reloaded event handler threw")
            End Try

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

        ''' <summary>
        ''' Phase 7-3b — returns a copy of the reference set with the
        ''' System.Net.* assemblies removed, so a plugin compiled
        ''' against it cannot resolve HttpClient/Socket/etc. Used for
        ''' capability-declaring plugins that didn't declare `network`.
        '''
        ''' Identification is by the reference's Display path/name
        ''' (Basic.Reference.Assemblies names each embedded reference
        ''' after its assembly, e.g. "System.Net.Http.dll"). We strip
        ''' the System.Net.* family plus the sockets/primitives that
        ''' carry the networking surface, and deliberately keep
        ''' everything else (notably System.Private.CoreLib, which
        ''' some networking types forward through but which stripping
        ''' would break the entire BCL).
        ''' </summary>
        Private Shared Function StripNetworkReferences(
                source As List(Of MetadataReference)) As List(Of MetadataReference)
            Dim result As New List(Of MetadataReference)
            For Each r In source
                Dim display = If(r.Display, "")
                Dim leaf = display
                Dim slash = display.LastIndexOfAny(New Char() {"/"c, "\"c})
                If slash >= 0 Then leaf = display.Substring(slash + 1)

                ' Strip the networking assemblies by leaf name. Matches
                ' "System.Net.dll", "System.Net.Http.dll",
                ' "System.Net.Sockets.dll", "System.Net.Primitives.dll",
                ' etc., without touching unrelated System.* assemblies.
                If leaf.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If
                result.Add(r)
            Next
            Return result
        End Function

    End Class

End Namespace