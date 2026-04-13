Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Configuration
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Core
Imports GSM.Data

' ============================================================
'  Program.vb - Manager Application Entry Point
'
'  Startup sequence:
'    1. Build the DI service container
'    2. Apply EF Core database migrations
'    3. Initialise the PluginRegistry (compile plugins)
'    4. Check for stale installation locks
'    5. Wire up services into MainForm
'    6. Start the metrics poll background timer
'    7. Show the main window
'
'  DI in WinForms:
'    WinForms predates DI, so we bootstrap it manually here.
'    There's no magic injection into forms — we resolve services
'    from the container and assign them as properties.
'    Forms that need services receive them via constructor
'    parameters or properties set by their caller.
'
'  EF Core note for first-timers:
'    On first run (or when the schema changes), EF applies
'    pending migrations automatically via MigrateAsync().
'    This creates the SQLite file and all tables if they
'    don't exist, or safely adds new columns/tables if they do.
'    You never need to manually run SQL.
' ============================================================

Module Program

    <STAThread>
    Sub Main()
        Application.SetHighDpiMode(HighDpiMode.SystemAware)
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        ' Build the DI container.
        Dim services = BuildServices()

        ' Run the async startup sequence synchronously before
        ' showing the main window. WinForms needs the UI thread
        ' to stay free so we use .GetAwaiter().GetResult() once.
        Dim startupResult = StartupAsync(services).GetAwaiter().GetResult()
        Dim mainForm = startupResult.Form
        Dim startupWarnings = startupResult.Warnings

        ' Show any startup warnings (stale locks, missing plugins etc).
        If startupWarnings.Any() Then
            Dim msg = String.Join(Environment.NewLine, startupWarnings)
            MessageBox.Show(msg, "Startup Warnings",
                MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End If

        Application.Run(mainForm)

        ' Clean up when the main window closes.
        CleanupAsync(services).GetAwaiter().GetResult()
    End Sub

    Private Function BuildServices() As ServiceProvider
        Dim services As New ServiceCollection()

        ' ---- Configuration ----
        ' Read from appsettings.json in the application directory.
        Dim configBuilder As New Microsoft.Extensions.Configuration.ConfigurationBuilder()
        Dim appSettingsPath = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json")
        configBuilder.AddJsonFile(appSettingsPath, optional:=True, reloadOnChange:=False)
        configBuilder.AddEnvironmentVariables("GSM_")
        Dim config = configBuilder.Build()

        ' ---- Database ----
        ' IDbContextFactory creates short-lived contexts on demand.
        ' This is the correct pattern for desktop apps (not web).
        Dim dbPath = IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "gsm.db")
        Dim connectionString = $"Data Source={dbPath}"

        services.AddDbContextFactory(Of GsmDbContext)(
            Sub(options) options.UseSqlite(connectionString))

        ' ---- Logging ----
        services.AddLogging(Sub(logging)
            logging.AddConsole()
            logging.SetMinimumLevel(LogLevel.Information)
        End Sub)

        ' ---- Core services ----
        ' AddSingleton = one instance for the whole app lifetime.
        ' Order doesn't matter - DI resolves dependencies automatically.
        services.AddSingleton(Of CredentialService)()
        services.AddSingleton(Of NodeHttpClientFactory)()
        services.AddSingleton(Of NotificationService)()
        services.AddSingleton(Of PluginRegistry)(
            Function(sp)
                ' PluginRegistry has several dependencies - resolve them
                ' from the container and pass them in.
                Return New PluginRegistry(
                    pluginsDirectory:=IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "plugins"),
                    logParserCoordinator:=sp.GetRequiredService(Of InstanceManager)(),
                    ringBufferStore:=sp.GetRequiredService(Of ManagerRingBufferStore)(),
                    orphanDetector:=sp.GetRequiredService(Of PluginOrphanDetector)(),
                    logger:=sp.GetRequiredService(Of ILogger(Of PluginRegistry))())
            End Function)
        services.AddSingleton(Of ManagerRingBufferStore)()
        services.AddSingleton(Of PluginOrphanDetector)()
        services.AddSingleton(Of InstanceManager)()
        services.AddSingleton(Of InstallationManager)()
        services.AddSingleton(Of AutomationEngine)()

        Return services.BuildServiceProvider()
    End Function

    Private Async Function StartupAsync(
            services As ServiceProvider) As Task(Of (Form As MainForm,
                                                      Warnings As List(Of String)))

        Dim warnings As New List(Of String)()

        ' ---- Step 1: Apply EF Core migrations ----
        ' MigrateAsync() is idempotent - safe to call every startup.
        ' On first run it creates the database and all tables.
        ' On subsequent runs it applies any new migrations.
        ' If the database is already up to date, it does nothing.
        Using scope As IServiceScope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(services)
            Dim db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(Of GsmDbContext)(scope.ServiceProvider)
            Await db.Database.MigrateAsync()

            ' Enable WAL mode for better concurrent performance.
            ' EF doesn't set this automatically.
            Await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;")
            Await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;")
        End Using

        ' ---- Step 2: Load plugins ----
        Dim pluginRegistry = services.GetRequiredService(Of PluginRegistry)()
        Dim reloadSummary = Await pluginRegistry.ReloadAsync(CancellationToken.None)

        If reloadSummary.Outcome = ReloadOutcome.NoFiles Then
            warnings.Add("No plugin files found in the plugins\ folder. " &
                         "Copy your plugin .vb files there and use " &
                         "Plugins → Reload Plugins to load them.")
        End If

        For Each orphan In reloadSummary.OrphanWarnings
            warnings.Add(orphan)
        Next

        ' ---- Step 3: Check for stale installation locks ----
        Dim installManager = services.GetRequiredService(Of InstallationManager)()
        Dim staleLocks = Await installManager.CheckStaleLocks(CancellationToken.None)
        warnings.AddRange(staleLocks)

        ' ---- Step 4: Start the automation engine ----
        Dim automationEngine = services.GetRequiredService(Of AutomationEngine)()
        Await automationEngine.StartAsync(CancellationToken.None)

        ' ---- Step 5: Start metrics poll ----
        Dim instanceManager = services.GetRequiredService(Of InstanceManager)()
        instanceManager.StartMetricsPoll(intervalSeconds:=30)

        ' ---- Step 6: Wire up MainForm ----
        Dim mainForm As New MainForm()
        mainForm.InstanceManager = instanceManager
        mainForm.InstallationManager = installManager
        mainForm.AutomationEngine = automationEngine
        mainForm.PluginRegistry = pluginRegistry
        mainForm.CredentialService = services.GetRequiredService(Of CredentialService)()
        mainForm.DbFactory = services.GetRequiredService(Of IDbContextFactory(Of GsmDbContext))()

        Return (mainForm, warnings)
    End Function

    Private Async Function CleanupAsync(services As ServiceProvider) As Task
        ' Stop the automation engine cleanly.
        Try
            Dim engine = services.GetRequiredService(Of AutomationEngine)()
            Await engine.StopAsync()
        Catch
        End Try

        services.Dispose()
    End Function

End Module


' ============================================================
'  MANAGER RING BUFFER STORE
'  The manager-side ring buffer that receives log lines from
'  the node SSE stream and feeds them to the log parser
'  coordinator. This is the manager's half of the checkpoint/
'  replay system used by hot-reload.
'
'  In production this would maintain an in-memory buffer
'  per instance, with overflow to the manager's SQLite.
'  The implementation here is a thin stub showing the
'  structure - the full implementation follows the same
'  pattern as the node's RingBufferStore.
' ============================================================

Public Class ManagerRingBufferStore
    Implements IRingBufferStore

    Private ReadOnly _buffers As New System.Collections.Concurrent.
        ConcurrentDictionary(Of String, System.Collections.Concurrent.ConcurrentQueue(Of BufferedLogLine))(
            StringComparer.OrdinalIgnoreCase)

    Public Sub Append(instanceId As String, line As BufferedLogLine)
        Dim buffer = _buffers.GetOrAdd(instanceId,
            Function(id) New System.Collections.Concurrent.ConcurrentQueue(Of BufferedLogLine)())
        buffer.Enqueue(line)
        ' Cap at 10,000 lines per instance.
        Do While buffer.Count > 10000
            Dim dropped As BufferedLogLine = Nothing
            buffer.TryDequeue(dropped)
        Loop
    End Sub

    Public Async Function ReadFromCheckpointAsync(
            instanceId As String,
            fromLineIndex As Long,
            cancellation As CancellationToken) As Task(Of IReadOnlyList(Of BufferedLogLine)) _
            Implements IRingBufferStore.ReadFromCheckpointAsync

        Await Task.CompletedTask
        Dim buffer As System.Collections.Concurrent.ConcurrentQueue(Of BufferedLogLine) = Nothing
        If Not _buffers.TryGetValue(instanceId, buffer) Then
            Return Array.Empty(Of BufferedLogLine)()
        End If
        Return buffer.Where(Function(l) l.LineIndex >= fromLineIndex).
                      OrderBy(Function(l) l.LineIndex).
                      ToList().AsReadOnly()
    End Function

End Class


' ============================================================
'  PLUGIN ORPHAN DETECTOR
'  Implements IOrphanDetector by querying the manager DB
'  for installations/instances with no matching plugin.
' ============================================================

Public Class PluginOrphanDetector
    Implements IOrphanDetector

    Private ReadOnly _dbFactory As IDbContextFactory(Of GsmDbContext)

    Public Sub New(dbFactory As IDbContextFactory(Of GsmDbContext))
        _dbFactory = dbFactory
    End Sub

    Public Async Function FindOrphansAsync(
            loadedGameIds As IReadOnlyList(Of String),
            cancellation As CancellationToken) As Task(Of IReadOnlyList(Of OrphanRecord)) _
            Implements IOrphanDetector.FindOrphansAsync

        Dim orphans As New List(Of OrphanRecord)()

        Using db = _dbFactory.CreateDbContext()
            Dim orphanInstalls = Await db.Installations.
                Where(Function(i) Not loadedGameIds.Contains(i.GameId)).
                ToListAsync(cancellation)

            For Each install In orphanInstalls
                orphans.Add(New OrphanRecord With {
                    .RecordType = "Installation",
                    .RecordId = install.InstallationId,
                    .DisplayName = install.DisplayName,
                    .GameId = install.GameId,
                    .IsCurrentlyRunning = False
                })
            Next

            Dim orphanInstances = Await db.Instances.
                Where(Function(i) Not loadedGameIds.Contains(i.GameId)).
                ToListAsync(cancellation)

            Dim runningStates = {"Running", "Starting", "Restarting"}
            For Each inst In orphanInstances
                orphans.Add(New OrphanRecord With {
                    .RecordType = "Instance",
                    .RecordId = inst.InstanceId,
                    .DisplayName = inst.DisplayName,
                    .GameId = inst.GameId,
                    .IsCurrentlyRunning = runningStates.Contains(inst.LastKnownState)
                })
            Next
        End Using

        Return orphans.AsReadOnly()
    End Function

End Class
