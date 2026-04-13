Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Runtime.Loader
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports GSM.Plugin
Imports GSM.Notification

' ============================================================
'  PluginRegistry
'
'  Responsibilities:
'    - Compile .vb files from the plugins\ folder via Roslyn
'    - Load compiled assemblies into isolated AssemblyLoadContexts
'    - Hot-reload on explicit operator request (UI button) without
'      stopping running instances:
'        1. Compile new assembly in fresh ALC
'        2. Drain active log parsers (flush + checkpoint)
'        3. Swap registry reference atomically
'        4. Replay ring buffer from checkpoint into new parser
'        5. Release old ALC reference → GC collects it
'    - Expose GetPlugin(gameId) and GetNotificationPlugin(pluginId)
'      which always return interface types, never concrete plugin types
'
'  Reload is MANUAL ONLY. The operator drops plugin files into the
'  plugins\ folder and clicks "Reload Plugins" in the UI.
'  There is no file watcher. This prevents spurious reloads from
'  partial writes, mid-edit saves, or editor auto-formatting.
'
'  CRITICAL HOT-RELOAD RULE:
'    Nothing outside this class may hold a reference to a concrete
'    plugin type (LastOasisPlugin, FactorioPlugin, etc).
'    All external code must use IGamePlugin / INotificationPlugin.
'    Holding a concrete type reference prevents the old ALC from
'    being collected and causes a memory leak on every hot-reload.
'    This class enforces the rule by only ever returning interfaces.
'
'  Thread safety:
'    _snapshot is an immutable record replaced atomically via
'    Volatile.Write. Readers always get a consistent view.
'    _reloadLock ensures only one reload runs at a time if the
'    user clicks the button repeatedly.
' ============================================================

Namespace GSM.Core

    Public Class PluginRegistry
        Implements IDisposable

        ' ---- Dependencies injected at construction ----
        Private ReadOnly _pluginsDirectory As String
        Private ReadOnly _logParserCoordinator As ILogParserCoordinator
        Private ReadOnly _ringBufferStore As IRingBufferStore
        Private ReadOnly _orphanDetector As IOrphanDetector
        Private ReadOnly _logger As IRegistryLogger

        ' ---- Reload serialisation ----
        ' Prevents a double-reload if the user clicks the button
        ' while a reload is already in progress.
        Private ReadOnly _reloadLock As New SemaphoreSlim(1, 1)

        ' ---- The live snapshot - replaced atomically on reload ----
        Private _snapshot As PluginSnapshot

        ' ---- Referenced assemblies for Roslyn compilation ----
        Private ReadOnly _metadataReferences As IReadOnlyList(Of MetadataReference)

        ' ---- Disposal ----
        Private _disposed As Boolean = False


        ' ============================================================
        '  CONSTRUCTION + STARTUP
        ' ============================================================

        Public Sub New(pluginsDirectory As String,
                       logParserCoordinator As ILogParserCoordinator,
                       ringBufferStore As IRingBufferStore,
                       orphanDetector As IOrphanDetector,
                       logger As IRegistryLogger)
            _pluginsDirectory = pluginsDirectory
            _logParserCoordinator = logParserCoordinator
            _ringBufferStore = ringBufferStore
            _orphanDetector = orphanDetector
            _logger = logger
            _metadataReferences = BuildMetadataReferences()
            _snapshot = PluginSnapshot.Empty
        End Sub

        ' Load all plugins from the plugins directory.
        ' Called once at manager startup. Also called when the operator
        ' clicks "Reload Plugins" in the UI.
        ' Returns a summary of what loaded and what failed, for display
        ' in the UI reload dialog.
        Public Async Function ReloadAsync(cancellation As CancellationToken) As Task(Of PluginReloadSummary)

            If Not _reloadLock.Wait(0) Then
                ' A reload is already in progress - tell the UI.
                Return PluginReloadSummary.AlreadyInProgress()
            End If

            Try
                If Not Directory.Exists(_pluginsDirectory) Then
                    Directory.CreateDirectory(_pluginsDirectory)
                    _logger.Info("PluginRegistry: created plugins directory")
                End If

                Dim vbFiles = Directory.GetFiles(_pluginsDirectory, "*.vb")
                _logger.Info($"PluginRegistry: reloading {vbFiles.Length} plugin file(s) " &
                             "(operator requested)")

                If vbFiles.Length = 0 Then
                    _logger.Info("PluginRegistry: no plugin files found - clearing registry")
                    Dim emptySnapshot = PluginSnapshot.Empty
                    Volatile.Write(_snapshot, emptySnapshot)
                    Return PluginReloadSummary.NoFiles()
                End If

                Return Await ReloadInternalAsync(vbFiles, cancellation)

            Finally
                _reloadLock.Release()
            End Try
        End Function


        ' ============================================================
        '  PUBLIC PLUGIN ACCESSORS
        '  These are the ONLY methods that return plugin instances.
        '  They always return interface types. Never concrete types.
        ' ============================================================

        ' Returns Nothing if no plugin is registered for this gameId.
        Public Function GetPlugin(gameId As String) As IGamePlugin
            Return _snapshot.GamePlugins.GetValueOrDefault(
                gameId.ToLowerInvariant())
        End Function

        Public Function GetAllPlugins() As IReadOnlyList(Of IGamePlugin)
            Return _snapshot.GamePlugins.Values.ToList().AsReadOnly()
        End Function

        Public Function GetNotificationPlugin(pluginId As String) As INotificationPlugin
            Return _snapshot.NotificationPlugins.GetValueOrDefault(
                pluginId.ToLowerInvariant())
        End Function

        Public Function GetAllNotificationPlugins() As IReadOnlyList(Of INotificationPlugin)
            Return _snapshot.NotificationPlugins.Values.ToList().AsReadOnly()
        End Function

        ' Returns the load status of every .vb file from the last reload.
        ' Used by the UI to show which plugins are active and any errors.
        Public Function GetLoadStatus() As IReadOnlyList(Of PluginLoadStatus)
            Return _snapshot.LoadStatuses.Values.ToList().AsReadOnly()
        End Function

        ' Returns True if a reload is currently in progress.
        ' Used by the UI to disable the reload button and show a spinner.
        Public ReadOnly Property IsReloading As Boolean
            Get
                Return _reloadLock.CurrentCount = 0
            End Get
        End Property


        ' ============================================================
        '  RELOAD PIPELINE
        ' ============================================================

        Private Async Function ReloadInternalAsync(vbFiles As String(),
                                                    cancellation As CancellationToken) As Task(Of PluginReloadSummary)

            ' ---- Step 1: Compile ----
            Dim compileResult = CompilePlugins(vbFiles)

            If compileResult.Assembly Is Nothing Then
                _logger.Warn("PluginRegistry: compilation failed - keeping existing plugins")
                ' Update statuses so the UI shows errors, but keep running plugins.
                Dim failedSnapshot = _snapshot.WithStatuses(compileResult.Statuses)
                Volatile.Write(_snapshot, failedSnapshot)
                Return PluginReloadSummary.CompileFailed(compileResult.Statuses)
            End If

            ' ---- Step 2: Discover plugin types ----
            Dim newGamePlugins As New Dictionary(Of String, IGamePlugin)(
                StringComparer.OrdinalIgnoreCase)
            Dim newNotificationPlugins As New Dictionary(Of String, INotificationPlugin)(
                StringComparer.OrdinalIgnoreCase)
            Dim discoveryErrors As New List(Of String)

            For Each t In compileResult.Assembly.GetTypes()
                If t.IsAbstract OrElse Not t.IsPublic Then Continue For

                If GetType(IGamePlugin).IsAssignableFrom(t) Then
                    Try
                        Dim plugin = CType(Activator.CreateInstance(t), IGamePlugin)
                        If newGamePlugins.ContainsKey(plugin.GameId) Then
                            Dim msg = $"Duplicate GameId '{plugin.GameId}' from {t.FullName} - skipped"
                            discoveryErrors.Add(msg)
                            _logger.Warn("PluginRegistry: " & msg)
                            Continue For
                        End If
                        newGamePlugins(plugin.GameId.ToLowerInvariant()) = plugin
                        _logger.Info($"PluginRegistry: registered '{plugin.DisplayName}' ({plugin.GameId})")
                    Catch ex As Exception
                        Dim msg = "Failed to instantiate " & t.FullName & ": " & ex.Message
                        discoveryErrors.Add(msg)
                        _logger.Error("PluginRegistry: " & msg)
                    End Try
                End If

                If GetType(INotificationPlugin).IsAssignableFrom(t) Then
                    Try
                        Dim plugin = CType(Activator.CreateInstance(t), INotificationPlugin)
                        If newNotificationPlugins.ContainsKey(plugin.PluginId) Then
                            Dim msg = $"Duplicate PluginId '{plugin.PluginId}' from {t.FullName} - skipped"
                            discoveryErrors.Add(msg)
                            _logger.Warn("PluginRegistry: " & msg)
                            Continue For
                        End If
                        newNotificationPlugins(plugin.PluginId.ToLowerInvariant()) = plugin
                        _logger.Info($"PluginRegistry: registered notification '{plugin.DisplayName}' ({plugin.PluginId})")
                    Catch ex As Exception
                        Dim msg = "Failed to instantiate " & t.FullName & ": " & ex.Message
                        discoveryErrors.Add(msg)
                        _logger.Error("PluginRegistry: " & msg)
                    End Try
                End If
            Next

            ' ---- Step 3: Drain active log parsers ----
            Dim checkpoints = Await _logParserCoordinator.DrainAllParsersAsync(cancellation)
            _logger.Info($"PluginRegistry: drained {checkpoints.Count} active log parser(s)")

            ' ---- Step 4: Swap snapshot atomically ----
            Dim oldSnapshot = _snapshot
            Dim newSnapshot = New PluginSnapshot(
                newGamePlugins,
                newNotificationPlugins,
                compileResult.Statuses,
                compileResult.LoadContext)
            Volatile.Write(_snapshot, newSnapshot)

            ' ---- Step 5: Replay ring buffer into new parsers ----
            For Each kvp In checkpoints
                Dim instanceId = kvp.Key
                Dim checkpoint = kvp.Value

                Dim newPlugin = newGamePlugins.GetValueOrDefault(
                    checkpoint.GameId.ToLowerInvariant())
                If newPlugin Is Nothing Then
                    _logger.Warn($"PluginRegistry: no plugin for '{checkpoint.GameId}' after reload - " &
                                  $"instance {instanceId} log parser not resumed")
                    Continue For
                End If

                Dim newParser = newPlugin.GetLogParser()
                If newParser Is Nothing Then Continue For

                Dim bufferedLines = Await _ringBufferStore.ReadFromCheckpointAsync(
                    instanceId, checkpoint.LineIndex, cancellation)

                For Each line In bufferedLines
                    newParser.ProcessLine(line.SourceId, line.Timestamp, line.Content)
                Next

                Await _logParserCoordinator.RegisterParserAsync(
                    instanceId, checkpoint.GameId, newParser, cancellation)

                _logger.Info($"PluginRegistry: resumed parser for {instanceId} " &
                             $"({bufferedLines.Count} lines replayed)")
            Next

            ' ---- Step 6: Release old ALC ----
            If oldSnapshot IsNot Nothing AndAlso oldSnapshot.LoadContext IsNot Nothing Then oldSnapshot.LoadContext.Unload()
            _logger.Info("PluginRegistry: reload complete")

            ' ---- Step 7: Detect orphaned installations and instances ----
            ' Check the database for any installations or instances whose GameId
            ' no longer has a loaded plugin. These records still exist and will
            ' continue to run if already started, but cannot be started again
            ' until a matching plugin is loaded.
            ' Surface these as warnings in the reload summary so the operator
            ' knows immediately rather than discovering it on next start attempt.
            Dim orphanWarnings As New List(Of String)
            Try
                Dim orphans = Await _orphanDetector.FindOrphansAsync(
                    newGamePlugins.Keys.ToList(), cancellation)

                For Each orphan In orphans
                    Dim msg = $"{orphan.RecordType} '{orphan.DisplayName}' " &
                              $"(id: {orphan.RecordId}) uses plugin '{orphan.GameId}' " &
                              $"which is no longer loaded. It cannot be started until " &
                              $"a plugin with GameId '{orphan.GameId}' is loaded."
                    orphanWarnings.Add(msg)
                    _logger.Warn("PluginRegistry: orphan detected - " & msg)
                Next

                If orphanWarnings.Count > 0 Then
                    _logger.Warn(
                        $"PluginRegistry: {orphanWarnings.Count} orphaned record(s) " &
                        "found. These installations/instances cannot be started.")
                End If
            Catch ex As Exception
                ' Never let orphan detection failure block the reload result.
                Dim errMsg = "PluginRegistry: orphan detection failed: " & ex.Message
                _logger.Error(errMsg)
            End Try

            Return PluginReloadSummary.Success(
                newGamePlugins.Count,
                newNotificationPlugins.Count,
                compileResult.Statuses,
                discoveryErrors,
                orphanWarnings)
        End Function


        ' ============================================================
        '  ROSLYN COMPILATION
        ' ============================================================

        Private Function CompilePlugins(vbFiles As String()) As CompilationResult
            Dim statuses As New Dictionary(Of String, PluginLoadStatus)
            Dim syntaxTrees As New List(Of SyntaxTree)

            ' Parse each file into a syntax tree.
            For Each filePath In vbFiles
                Dim fileName = Path.GetFileName(filePath)
                Try
                    Dim source = File.ReadAllText(filePath)
                    Dim tree = VisualBasicSyntaxTree.ParseText(
                        source,
                        New VisualBasicParseOptions(LanguageVersion.Latest),
                        path:=filePath)

                    ' Check for parse errors before attempting compilation.
                    Dim parseErrors = tree.GetDiagnostics().
                                          Where(Function(d) d.Severity = DiagnosticSeverity.Error).
                                          ToList()

                    If parseErrors.Any() Then
                        statuses(filePath) = PluginLoadStatus.ParseFailed(
                            fileName,
                            parseErrors.Select(Function(d) d.ToString()).ToList())
                        _logger.Error($"PluginRegistry: parse errors in {fileName}:")
                        For Each parseDiagnostic As Diagnostic In parseErrors
                            _logger.Error("  " & parseDiagnostic.ToString())
                        Next
                        Continue For
                    End If

                    syntaxTrees.Add(tree)
                    ' Status will be updated to Loaded or CompileFailed below.

                Catch ex As Exception
                    statuses(filePath) = PluginLoadStatus.IOFailed(fileName, ex.Message)
                    Dim errMsg = "PluginRegistry: failed to read " & fileName & ": " & ex.Message
                    _logger.Error(errMsg)
                End Try
            Next

            If Not syntaxTrees.Any() Then
                Return CompilationResult.Failed(statuses)
            End If

            ' Compile all parsed trees together into a single in-memory assembly.
            Dim assemblyName = "GSM.Plugins." & Guid.NewGuid().ToString("N")
            ' Plugin code should be trusted - it's written by the operator.
            ' Restrict to avoid accidental unsafe operations.
            Dim compilation = VisualBasicCompilation.Create(
                assemblyName,
                syntaxTrees,
                _metadataReferences,
                New VisualBasicCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel:=OptimizationLevel.Release,
                    deterministic:=True))

            Using peStream As New IO.MemoryStream()
                Dim emitResult = compilation.Emit(peStream)

                If Not emitResult.Success Then
                    ' Group errors by source file for clearer status reporting.
                    Dim errorsByFile = emitResult.Diagnostics.
                        Where(Function(d) d.Severity = DiagnosticSeverity.Error).
                        GroupBy(Function(d) If(d.Location.SourceTree?.FilePath, "(unknown)"))

                    For Each fileErrors In errorsByFile
                        Dim fileName = Path.GetFileName(fileErrors.Key)
                        statuses(fileErrors.Key) = PluginLoadStatus.CompileFailed(
                            fileName,
                            fileErrors.Select(Function(d) d.ToString()).ToList())
                        _logger.Error($"PluginRegistry: compile errors in {fileName}:")
                        For Each compileDiagnostic In fileErrors
                            _logger.Error("  " & compileDiagnostic.ToString())
                        Next
                    Next

                    Return CompilationResult.Failed(statuses)
                End If

                ' Load the compiled assembly into a fresh, collectible ALC.
                ' Collectible = True is what enables hot-reload: the ALC and
                ' everything it loaded can be unloaded when we drop the reference.
                peStream.Seek(0, IO.SeekOrigin.Begin)
                Dim alc = New PluginAssemblyLoadContext(collectible:=True)
                Dim assembly = alc.LoadFromStream(peStream)

                ' Mark all successfully compiled files as loaded.
                For Each tree In syntaxTrees
                    Dim filePath = tree.FilePath
                    If Not statuses.ContainsKey(filePath) Then
                        statuses(filePath) = PluginLoadStatus.Loaded(
                            Path.GetFileName(filePath))
                    End If
                Next

                _logger.Info($"PluginRegistry: compiled {syntaxTrees.Count} file(s) " &
                             $"into assembly '{assemblyName}'")

                Return New CompilationResult(assembly, alc, statuses)
            End Using
        End Function

        ' Build the set of metadata references needed to compile plugin code.
        ' This includes the current runtime assemblies and the GSM contract assemblies.
        Private Function BuildMetadataReferences() As IReadOnlyList(Of MetadataReference)
            Dim refs As New List(Of MetadataReference)

            ' Standard runtime references.
            Dim trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            If trustedAssemblies IsNot Nothing Then
                For Each path In CStr(trustedAssemblies).Split(IO.Path.PathSeparator)
                    If File.Exists(path) Then
                        refs.Add(MetadataReference.CreateFromFile(path))
                    End If
                Next
            End If

            ' GSM contract assemblies - IGamePlugin, INotificationPlugin etc.
            ' Plugins implement these interfaces so they must be referenceable.
            Dim contractAssemblies = {
                GetType(IGamePlugin).Assembly,
                GetType(INotificationPlugin).Assembly,
                GetType(Object).Assembly,
                Assembly.GetExecutingAssembly()
            }

            For Each asm In contractAssemblies.Distinct()
                If Not String.IsNullOrEmpty(asm.Location) Then
                    refs.Add(MetadataReference.CreateFromFile(asm.Location))
                End If
            Next

            Return refs.DistinctBy(Function(r) r.Display).ToList().AsReadOnly()
        End Function


        ' ============================================================
        '  DISPOSAL
        ' ============================================================

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            If _snapshot IsNot Nothing AndAlso _snapshot.LoadContext IsNot Nothing Then _snapshot.LoadContext.Unload()
            _reloadLock.Dispose()
            _logger.Info("PluginRegistry: disposed")
        End Sub

    End Class


    ' ============================================================
    '  PLUGIN RELOAD SUMMARY
    '  Returned by ReloadAsync and shown in the UI reload dialog
    '  so the operator knows exactly what happened.
    ' ============================================================

    Public Class PluginReloadSummary
        Public Property Outcome As ReloadOutcome
        Public Property GamePluginsLoaded As Integer
        Public Property NotificationPluginsLoaded As Integer
        Public Property FileStatuses As IReadOnlyList(Of PluginLoadStatus)
        Public Property DiscoveryErrors As IReadOnlyList(Of String)
        ' Installations and instances whose GameId has no loaded plugin.
        ' These records still exist in the DB and running instances keep
        ' running, but they cannot be started until a matching plugin loads.
        Public Property OrphanWarnings As IReadOnlyList(Of String)
        Public Property Message As String

        Public ReadOnly Property HasOrphans As Boolean
            Get
                Return OrphanWarnings IsNot Nothing AndAlso OrphanWarnings.Count > 0
            End Get
        End Property

        Public Shared Function Success(gamePlug As Integer,
                                       notifPlug As Integer,
                                       statuses As Dictionary(Of String, PluginLoadStatus),
                                       discoveryErrors As List(Of String),
                                       Optional orphanWarnings As List(Of String) = Nothing) As PluginReloadSummary
            Dim orphans = If(orphanWarnings, New List(Of String)())
            Dim msg = $"Loaded {gamePlug} game plugin(s) and {notifPlug} notification plugin(s)."
            If orphans.Count > 0 Then
                msg &= $" Warning: {orphans.Count} installation(s)/instance(s) have no " &
                       "matching plugin and cannot be started."
            End If
            Return New PluginReloadSummary With {
                .Outcome = ReloadOutcome.Success,
                .GamePluginsLoaded = gamePlug,
                .NotificationPluginsLoaded = notifPlug,
                .FileStatuses = statuses.Values.ToList().AsReadOnly(),
                .DiscoveryErrors = discoveryErrors.AsReadOnly(),
                .OrphanWarnings = orphans.AsReadOnly(),
                .Message = msg
            }
        End Function

        Public Shared Function CompileFailed(
                statuses As Dictionary(Of String, PluginLoadStatus)) As PluginReloadSummary
            Dim errorCount As Integer = 0
            For Each statusEntry In statuses.Values
                If statusEntry.State <> PluginLoadState.Loaded Then
                    errorCount += 1
                End If
            Next
            Return New PluginReloadSummary With {
                .Outcome = ReloadOutcome.CompileFailed,
                .FileStatuses = statuses.Values.ToList().AsReadOnly(),
                .DiscoveryErrors = Array.Empty(Of String)(),
                .OrphanWarnings = Array.Empty(Of String)(),
                .Message = $"Compilation failed in {errorCount} file(s). " &
                           "Existing plugins remain active. See errors below."
            }
        End Function

        Public Shared Function NoFiles() As PluginReloadSummary
            Return New PluginReloadSummary With {
                .Outcome = ReloadOutcome.NoFiles,
                .FileStatuses = Array.Empty(Of PluginLoadStatus)(),
                .DiscoveryErrors = Array.Empty(Of String)(),
                .OrphanWarnings = Array.Empty(Of String)(),
                .Message = "No .vb files found in the plugins directory. " &
                           "Drop plugin files into the plugins\ folder and reload again."
            }
        End Function

        Public Shared Function AlreadyInProgress() As PluginReloadSummary
            Return New PluginReloadSummary With {
                .Outcome = ReloadOutcome.AlreadyInProgress,
                .FileStatuses = Array.Empty(Of PluginLoadStatus)(),
                .DiscoveryErrors = Array.Empty(Of String)(),
                .OrphanWarnings = Array.Empty(Of String)(),
                .Message = "A reload is already in progress. Please wait."
            }
        End Function
    End Class

    Public Enum ReloadOutcome
        Success
        CompileFailed
        NoFiles
        AlreadyInProgress
    End Enum


    ' ============================================================
    '  ASSEMBLY LOAD CONTEXT
    '  One per compiled plugin batch. Collectible so it can be
    '  unloaded when the next hot-reload completes.
    ' ============================================================

    Friend Class PluginAssemblyLoadContext
        Inherits AssemblyLoadContext

        Public Sub New(collectible As Boolean)
            MyBase.New(name:="GSM.Plugins", isCollectible:=collectible)
        End Sub

        Protected Overrides Function Load(assemblyName As AssemblyName) As Assembly
            ' Resolve plugin dependencies from the default context.
            ' This makes the GSM contract types (IGamePlugin etc) resolve
            ' to the same assembly as in the host - essential for interface
            ' compatibility. Without this, IsAssignableFrom returns False
            ' because the host and plugin see different type identities.
            Return Nothing  ' Returning Nothing delegates to the default ALC
        End Function

    End Class


    ' ============================================================
    '  PLUGIN SNAPSHOT
    '  Immutable record of all loaded plugins at a point in time.
    '  Replaced atomically on each hot-reload.
    ' ============================================================

    Friend Class PluginSnapshot
        Public ReadOnly Property GamePlugins As IReadOnlyDictionary(Of String, IGamePlugin)
        Public ReadOnly Property NotificationPlugins As IReadOnlyDictionary(Of String, INotificationPlugin)
        Public ReadOnly Property LoadStatuses As IReadOnlyDictionary(Of String, PluginLoadStatus)
        Public ReadOnly Property LoadContext As PluginAssemblyLoadContext

        Public Sub New(gamePlug As Dictionary(Of String, IGamePlugin),
                       notifPlug As Dictionary(Of String, INotificationPlugin),
                       statuses As Dictionary(Of String, PluginLoadStatus),
                       alc As PluginAssemblyLoadContext)
            GamePlugins = gamePlug
            NotificationPlugins = notifPlug
            LoadStatuses = statuses
            LoadContext = alc
        End Sub

        ' Returns a new snapshot with updated statuses but the same plugins.
        ' Used when compilation fails - we keep running plugins but surface errors.
        Public Function WithStatuses(
                newStatuses As Dictionary(Of String, PluginLoadStatus)) As PluginSnapshot
            Return New PluginSnapshot(
                New Dictionary(Of String, IGamePlugin)(GamePlugins),
                New Dictionary(Of String, INotificationPlugin)(NotificationPlugins),
                newStatuses,
                LoadContext)
        End Function

        Public Shared ReadOnly Property Empty As PluginSnapshot
            Get
                Return New PluginSnapshot(
                    New Dictionary(Of String, IGamePlugin),
                    New Dictionary(Of String, INotificationPlugin),
                    New Dictionary(Of String, PluginLoadStatus),
                    Nothing)
            End Get
        End Property
    End Class


    ' ============================================================
    '  COMPILATION RESULT
    ' ============================================================

    Friend Class CompilationResult
        Public ReadOnly Property Assembly As Assembly
        Public ReadOnly Property LoadContext As PluginAssemblyLoadContext
        Public ReadOnly Property Statuses As Dictionary(Of String, PluginLoadStatus)

        Public Sub New(assembly As Assembly,
                       alc As PluginAssemblyLoadContext,
                       statuses As Dictionary(Of String, PluginLoadStatus))
            Me.Assembly = assembly
            Me.LoadContext = alc
            Me.Statuses = statuses
        End Sub

        Public Shared Function Failed(
                statuses As Dictionary(Of String, PluginLoadStatus)) As CompilationResult
            Return New CompilationResult(Nothing, Nothing, statuses)
        End Function
    End Class


    ' ============================================================
    '  PLUGIN LOAD STATUS
    '  Surfaced in the manager UI so operators know exactly which
    '  plugin files loaded successfully and what errors occurred.
    ' ============================================================

    Public Class PluginLoadStatus
        Public Property FileName As String
        Public Property State As PluginLoadState
        Public Property Errors As IReadOnlyList(Of String)
        Public Property LoadedAt As DateTime?

        Public Shared Function Loaded(fileName As String) As PluginLoadStatus
            Return New PluginLoadStatus With {
                .FileName = fileName,
                .State = PluginLoadState.Loaded,
                .Errors = Array.Empty(Of String)(),
                .LoadedAt = DateTime.UtcNow
            }
        End Function

        Public Shared Function ParseFailed(fileName As String,
                                           errors As List(Of String)) As PluginLoadStatus
            Return New PluginLoadStatus With {
                .FileName = fileName,
                .State = PluginLoadState.ParseFailed,
                .Errors = errors.AsReadOnly()
            }
        End Function

        Public Shared Function CompileFailed(fileName As String,
                                             errors As List(Of String)) As PluginLoadStatus
            Return New PluginLoadStatus With {
                .FileName = fileName,
                .State = PluginLoadState.CompileFailed,
                .Errors = errors.AsReadOnly()
            }
        End Function

        Public Shared Function IOFailed(fileName As String,
                                        message As String) As PluginLoadStatus
            Return New PluginLoadStatus With {
                .FileName = fileName,
                .State = PluginLoadState.IOFailed,
                .Errors = {message}.AsReadOnly()
            }
        End Function
    End Class

    Public Enum PluginLoadState
        Loaded
        ParseFailed
        CompileFailed
        IOFailed
    End Enum


    ' ============================================================
    '  LOG PARSER COORDINATOR (interface - implemented in Core)
    '  Abstracts the manager's live log parser tracking so the
    '  registry doesn't depend on the full instance management
    '  subsystem. Implemented by InstanceManager in Core.
    ' ============================================================

    Public Interface ILogParserCoordinator

        ' Drain all active log parsers before a hot-reload swap.
        ' Returns a checkpoint per instance: the ring buffer line
        ' index up to which the old parser has processed, and the
        ' gameId needed to instantiate a replacement parser.
        ' After this call returns, the old parsers receive no new lines.
        Function DrainAllParsersAsync(
            cancellation As CancellationToken) As Task(Of IReadOnlyDictionary(Of String, LogParserCheckpoint))

        ' Register a new parser as the active one for an instance.
        ' Called after the snapshot swap and ring buffer replay.
        ' From this point the instance's stdout stream feeds the new parser.
        Function RegisterParserAsync(instanceId As String,
                                     gameId As String,
                                     parser As ILogParser,
                                     cancellation As CancellationToken) As Task

    End Interface

    Public Class LogParserCheckpoint
        Public Property InstanceId As String
        Public Property GameId As String
        ' The index of the last line the old parser processed.
        ' The new parser replays from this position onwards.
        Public Property LineIndex As Long
    End Class


    ' ============================================================
    '  RING BUFFER STORE (interface - implemented in Core)
    '  Abstracts reading buffered log lines for replay after reload.
    '  Implemented by the node communication layer in Core which
    '  maintains per-instance ring buffers in the manager's SQLite.
    ' ============================================================

    Public Interface IRingBufferStore

        ' Read all buffered lines for an instance from a given
        ' line index onwards. Used during hot-reload to replay
        ' lines the old parser already saw into the new parser,
        ' plus any lines that arrived during the swap window.
        Function ReadFromCheckpointAsync(
            instanceId As String,
            fromLineIndex As Long,
            cancellation As CancellationToken) As Task(Of IReadOnlyList(Of BufferedLogLine))

    End Interface

    Public Class BufferedLogLine
        Public Property LineIndex As Long
        Public Property InstanceId As String
        Public Property SourceId As String      ' "stdout", "logfile" etc
        Public Property Timestamp As DateTime
        Public Property Content As String
    End Class


    ' ============================================================
    '  REGISTRY LOGGER (interface - implemented in Core)
    '  Thin abstraction so the registry doesn't depend on a
    '  specific logging framework. Implemented by whatever logger
    '  the manager uses (Serilog, NLog, built-in, etc).
    ' ============================================================

    Public Interface IRegistryLogger
        Sub Info(message As String)
        Sub Warn(message As String)
        Sub [Error](message As String)
    End Interface

    ' ============================================================
    '  ORPHAN DETECTOR (interface - implemented in Core)
    '  Checks the manager DB for installations and instances
    '  whose GameId has no corresponding loaded plugin.
    '  Implemented by PluginOrphanDetector in GSM.Core which
    '  has access to GsmDbContext.
    ' ============================================================

    Public Interface IOrphanDetector

        ' Returns all installations and instances in the DB whose
        ' GameId is not in the provided set of loaded plugin IDs.
        ' Called after every successful reload to surface orphans
        ' in the PluginReloadSummary shown to the operator.
        Function FindOrphansAsync(
            loadedGameIds As IReadOnlyList(Of String),
            cancellation As CancellationToken) As Task(Of IReadOnlyList(Of OrphanRecord))

    End Interface

    Public Class OrphanRecord
        ' "Installation" or "Instance"
        Public Property RecordType As String
        Public Property RecordId As String
        Public Property DisplayName As String
        ' The GameId that has no matching plugin
        Public Property GameId As String
        ' Whether this instance is currently running on its node.
        ' Running instances are not in danger - they just can't be
        ' restarted until the plugin is restored.
        Public Property IsCurrentlyRunning As Boolean
    End Class

End Namespace
