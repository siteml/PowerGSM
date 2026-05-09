Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Reflection
Imports System.Threading
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Manager.Core
Imports GSM.Node.Api
Imports GSM.Plugin

' ============================================================
'  GSM.Manager — Entry point
'
'  Phase 3 skeleton: creates the database, opens the main form.
'  Core services (PluginRegistry, InstanceManager, AutomationEngine)
'  are added in Phase 4.
' ============================================================

Namespace GSM.Manager

    Module ManagerProgram

        ''' <summary>
        ''' Application-wide service provider. Built once at startup,
        ''' available to all forms and services.
        ''' </summary>
        Public Property Services As IServiceProvider

        <STAThread>
        Sub Main()

            Application.SetHighDpiMode(HighDpiMode.SystemAware)
            Application.EnableVisualStyles()
            Application.SetCompatibleTextRenderingDefault(False)

            ' ---- Build DI container ----
            Dim serviceCollection As New ServiceCollection()

            ' Logging
            '
            ' WinForms apps don't have an attached console, so AddConsole
            ' silently drops everything — messages exist nowhere unless
            ' something else captures them. FileLoggerProvider writes
            ' to logs/manager-YYYY-MM-DD.log next to the binary.
            '
            ' Category filters keep the file size sane. EF Core logs
            ' every executed SQL statement at Information level under
            ' Microsoft.EntityFrameworkCore.*, and ASP.NET / HTTP
            ' lifecycle events fall under Microsoft.* and System.*.
            ' Without these filters the file grew to ~90 MB/day on
            ' a moderately active manager (95% framework chatter,
            ' 5% application). Clamping framework categories to
            ' Warning leaves us with our own GSM.Manager.*
            ' Information lines plus genuine framework warnings
            ' — useful diagnostic content, manageable volume.
            Dim logsDir = IO.Path.Combine(AppContext.BaseDirectory, "logs")
            Dim fileLogProvider As New FileLoggerProvider(logsDir, LogLevel.Information, "manager-")

            ' Retention pass at startup. 30-day window picked to
            ' cover "that thing happened a few weeks ago, can you
            ' check the logs" without unbounded growth. Adjust if
            ' the diagnostic horizon needs to be longer or the
            ' disk pressure is real. Daily rotation means at most
            ' one file is removed per startup until steady state.
            fileLogProvider.PruneOldLogs(retentionDays:=30)

            serviceCollection.AddLogging(Sub(cfg)
                                             cfg.AddConsole()
                                             cfg.AddProvider(fileLogProvider)
                                             cfg.SetMinimumLevel(LogLevel.Information)
                                             cfg.AddFilter("Microsoft", LogLevel.Warning)
                                             cfg.AddFilter("System", LogLevel.Warning)
                                         End Sub)

            ' Database
            serviceCollection.AddDbContext(Of GsmDbContext)(
                Sub(opts)
                    opts.UseSqlite("Data Source=gsm.db")
                End Sub,
                ServiceLifetime.Transient)

            ' Manager-side log buffer
            serviceCollection.AddSingleton(Of ManagerRingBufferStore)()

            ' Orphan detector
            serviceCollection.AddSingleton(Of PluginOrphanDetector)()

            ' Register BEFORE InstanceManager / InstallationManager, since
            ' those now take NotificationEmitter as a ctor parameter.
            serviceCollection.AddSingleton(Of NotificationEmitter)()
            serviceCollection.AddSingleton(Of DiscordWebhookPlugin)()

            ' Phase 5d-1 — Discord bot plugin. Coexists with the
            ' webhook plugin (both are independent INotificationPlugin
            ' implementations); the bot maintains persistent control
            ' panels in addition to acting as a transport for outbound
            ' notifications (5d-4). Registered as singleton so the
            ' DiscordBotForm can resolve it for Test Connection +
            ' panel reload.
            serviceCollection.AddSingleton(Of DiscordBotPlugin)()

            ' ---- Phase 4 Core services ----
            serviceCollection.AddSingleton(Of NodeHttpClientFactory)()
            serviceCollection.AddSingleton(Of CredentialService)()
            serviceCollection.AddSingleton(Of PluginRegistry)()
            serviceCollection.AddSingleton(Of InstanceManager)()
            serviceCollection.AddSingleton(Of InstallationManager)()
            serviceCollection.AddSingleton(Of NotificationService)()
            serviceCollection.AddSingleton(Of AutomationEngine)()
            serviceCollection.AddSingleton(Of ChatRetentionPruner)()
            serviceCollection.AddSingleton(Of VersionCheckService)()
            serviceCollection.AddSingleton(Of HistoryQueryService)()
            serviceCollection.AddSingleton(Of RestartCoordinator)()

            ' Build provider
            Services = serviceCollection.BuildServiceProvider()

            ' Self-identifying startup line. Reads this assembly's
            ' InformationalVersion (set indirectly via
            ' Directory.Build.props' Version property) and emits a
            ' Manager-version line at Information level so log files
            ' name what produced them. Useful when diagnosing issues
            ' across mixed Manager/Node versions — the file says
            ' "GSM.Manager 0.1.0 starting" before any other activity.
            ' Logging extension methods require Imports
            ' Microsoft.Extensions.Logging which is already present.
            Try
                Dim bootstrapLogger = Services.
                    GetRequiredService(Of ILoggerFactory)().
                    CreateLogger("GSM.Manager")
                Dim asm = GetType(ManagerRingBufferStore).Assembly
                Dim infoAttr = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
                Dim build As String = If(infoAttr?.InformationalVersion,
                                          asm.GetName().Version?.ToString(3))
                If String.IsNullOrEmpty(build) Then build = "0.0.0"
                bootstrapLogger.LogInformation(
                    "GSM.Manager {Build} starting (Protocol v{Protocol}, Contracts v{Contracts})",
                    build, NodeApiContract.ProtocolVersion,
                    NodeApiContract.ContractsVersion)
            Catch
                ' Logging failure must never block startup.
            End Try

            ' Ensure database is up-to-date with current schema.
            ' Migrate() applies any pending EF migrations — creates
            ' the DB if it doesn't exist, adds new tables/columns
            ' if we've added migrations since the last run, or no-ops
            ' if we're current. This replaces the older
            ' EnsureCreated() which didn't track migration history
            ' and couldn't evolve the schema once created.
            Using scope = Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                db.Database.Migrate()
            End Using

            ' Load plugins on startup
            Dim registry = Services.GetRequiredService(Of PluginRegistry)()
            Dim orphanDetector = Services.GetRequiredService(Of PluginOrphanDetector)()
            Dim pluginSummary = registry.ReloadAll(orphanDetector)
            If pluginSummary.CompilationErrors.Count > 0 Then
                Dim errMsg = $"{pluginSummary.CompilationErrors.Count} plugin compilation error(s):" & vbCrLf
                For Each compErr In pluginSummary.CompilationErrors
                    errMsg &= $"  {compErr.FileName}: {compErr.Message}" & vbCrLf
                Next
                MessageBox.Show(errMsg, "Plugin Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If

            ' Register the Discord plugin with the NotificationService so
            ' that BroadcastAsync reaches it. Blocks startup until the
            ' plugin has its first config snapshot — otherwise early
            ' events (from log streams reconnecting below, or from
            ' anything the MainForm triggers on Shown) would be emitted
            ' before the plugin knows which destinations are configured
            ' and would silently drop.
            '
            ' RegisterPluginAsync handles InitialiseAsync internally:
            ' it loads the plugin's config row from the DB and passes
            ' the NotificationService itself as the IRemoteCommandHandler.
            ' (Webhooks ignore the handler since they're write-only;
            ' the bot uses it for the Manage flow's command dispatch.)
            ' Earlier startup code called InitialiseAsync explicitly
            ' before RegisterPluginAsync, which double-initialised
            ' both plugins — visible as duplicate "config reloaded"
            ' lines in the webhook plugin and a connect/disconnect/
            ' reconnect cycle in the bot. The pre-init also passed an
            ' empty config dict, so the first pass loaded zero
            ' destinations — useless work that the second pass had to
            ' redo with the real config.
            Dim discordPlugin = Services.GetRequiredService(Of DiscordWebhookPlugin)()
            Dim notifications = Services.GetRequiredService(Of NotificationService)()
            Dim startupLogger = Services.
                GetRequiredService(Of ILoggerFactory)().
                CreateLogger("Startup")
            Try
                notifications.RegisterPluginAsync(
                    discordPlugin, CancellationToken.None).
                    GetAwaiter().GetResult()
            Catch ex As Exception
                ' Never fatal — Manager runs with or without Discord.
                startupLogger.LogWarning(ex, "Failed to register Discord plugin at startup")
            End Try

            ' Phase 5d-1 — register the Discord bot plugin. Same
            ' pattern as the webhook plugin: best-effort, a failure
            ' (bad token, Discord unreachable, missing config) logs
            ' a warning and leaves the Manager running.
            Dim discordBotPlugin = Services.GetRequiredService(Of DiscordBotPlugin)()
            Try
                notifications.RegisterPluginAsync(
                    discordBotPlugin, CancellationToken.None).
                    GetAwaiter().GetResult()
            Catch ex As Exception
                startupLogger.LogWarning(ex, "Failed to register Discord bot plugin at startup")
            End Try

            ' After services built, before Application.Run(mainForm)
            Dim mgr = Services.GetService(Of InstanceManager)()

            ' Wire the restart coordinator to the instance manager.
            ' Deferred bind (rather than ctor injection) breaks the
            ' cycle: InstanceManager calls INTO RestartCoordinator
            ' on TileLoaded events, and RestartCoordinator reads
            ' live state FROM InstanceManager during ready-signal
            ' waits. Ctor injection in both directions would
            ' deadlock service construction.
            Dim restartCoordinator = Services.GetService(Of RestartCoordinator)()
            If restartCoordinator IsNot Nothing AndAlso mgr IsNot Nothing Then
                restartCoordinator.AttachInstanceManager(mgr)
                mgr.AttachRestartCoordinator(restartCoordinator)
            End If

            If mgr IsNot Nothing Then
                Task.Run(Async Function()
                             Await mgr.ReconnectLogStreamsAsync()
                         End Function)

                ' Kick off the background poller so crash / crash-loop
                ' state transitions get detected for ALL instances, not
                ' just whichever one the user has an open detail tab for.
                mgr.StartBackgroundPolling()
            End If

            ' Start the chat-retention pruner. Idempotent + fail-soft
            ' — pruner blow-ups log a warning but don't affect the app.
            Dim pruner = Services.GetService(Of ChatRetentionPruner)()
            If pruner IsNot Nothing Then
                pruner.Start()
            End If

            ' Start the version-check service — polls each installation
            ' on a 60-minute interval and raises VersionMismatch events
            ' for rules that subscribed to them. Must start AFTER the
            ' AutomationEngine because the service raises events into it.
            Dim versionCheck = Services.GetService(Of VersionCheckService)()

            ' Start the automation engine. Loads rules from the DB
            ' and kicks off cron timers. Must happen AFTER services
            ' are fully wired (coordinator + instance manager
            ' attached above) because rule actions may depend on
            ' those singletons at first fire.
            Dim engine = Services.GetService(Of AutomationEngine)()
            Try
                engine?.Start()
            Catch ex As Exception
                startupLogger.LogWarning(ex, "AutomationEngine.Start threw at startup")
            End Try

            ' VersionCheckService starts AFTER the engine because it
            ' calls AutomationEngine.RaiseVersionMismatchAsync when
            ' it detects mismatches. If the engine isn't started
            ' first, those calls would no-op silently.
            If versionCheck IsNot Nothing Then
                Try
                    versionCheck.Start()
                Catch ex As Exception
                    startupLogger.LogWarning(ex, "VersionCheckService.Start threw at startup")
                End Try
            End If

            ' Launch main form
            Dim mainForm As New UI.MainForm()
            mainForm.SetStatus($"Plugins: {pluginSummary.LoadedPlugins.Count} loaded, {pluginSummary.CompilationErrors.Count} errors")
            Application.Run(mainForm)

            ' Application.Run blocks until MainForm closes. Clean-shutdown
            ' hooks go here — in reverse dependency order relative to startup.
            Try
                If versionCheck IsNot Nothing Then
                    versionCheck.StopAsync().GetAwaiter().GetResult()
                End If
            Catch
                ' Best-effort shutdown.
            End Try

            Try
                engine?.Stop()
            Catch
            End Try

            Try
                If pruner IsNot Nothing Then
                    pruner.StopAsync().GetAwaiter().GetResult()
                End If
            Catch
                ' Best-effort shutdown — process is exiting anyway.
            End Try

            Try
                If mgr IsNot Nothing Then
                    mgr.StopBackgroundPollingAsync().GetAwaiter().GetResult()
                End If
            Catch
            End Try

            ' Phase 5d-1 — disconnect the Discord bot. Done last
            ' so any state-change notifications fired during other
            ' shutdown steps still get a chance to refresh panels
            ' before the gateway closes. Best-effort; never blocks
            ' process exit.
            Try
                Dim botShutdown = Services.GetService(Of DiscordBotPlugin)()
                If botShutdown IsNot Nothing Then
                    botShutdown.ShutdownAsync(CancellationToken.None).
                        GetAwaiter().GetResult()
                End If
            Catch
            End Try

        End Sub

    End Module

    ' ============================================================
    '  ManagerRingBufferStore
    '
    '  Manager-side ring buffer for log lines received from nodes.
    '  Each instance gets its own buffer. Used by the log viewer
    '  panel to display live logs without re-querying the node.
    ' ============================================================

    Public Class ManagerRingBufferStore

        Private ReadOnly _buffers As New ConcurrentDictionary(Of String, ManagerInstanceBuffer)

        Private Const DefaultCapacity As Integer = 4096

        ''' <summary>
        ''' Appends a log line received from a node for the given instance.
        ''' </summary>
        Public Sub Append(instanceId As String, line As LogLine)
            Dim buf = _buffers.GetOrAdd(instanceId,
                Function(id) New ManagerInstanceBuffer(id, DefaultCapacity))
            buf.Add(line)
        End Sub

        ''' <summary>
        ''' Returns the most recent log lines for an instance.
        ''' </summary>
        Public Function GetTail(instanceId As String,
                                count As Integer) As IReadOnlyList(Of LogLine)
            Dim buf As ManagerInstanceBuffer = Nothing
            If Not _buffers.TryGetValue(instanceId, buf) Then
                Return Array.Empty(Of LogLine)()
            End If
            Return buf.GetTail(count)
        End Function

        ''' <summary>
        ''' Removes the buffer for an instance.
        ''' </summary>
        Public Sub RemoveBuffer(instanceId As String)
            Dim removed As ManagerInstanceBuffer = Nothing
            _buffers.TryRemove(instanceId, removed)
        End Sub

    End Class

    Friend Class ManagerInstanceBuffer

        Private ReadOnly _instanceId As String
        Private ReadOnly _ring() As LogLine
        Private _writePos As Long = 0
        Private ReadOnly _lock As New Object()

        Public Sub New(instanceId As String, capacity As Integer)
            _instanceId = instanceId
            ReDim _ring(capacity - 1)
        End Sub

        Public Sub Add(line As LogLine)
            SyncLock _lock
                _ring(CInt(_writePos Mod _ring.Length)) = line
                _writePos += 1
            End SyncLock
        End Sub

        Public Function GetTail(count As Integer) As IReadOnlyList(Of LogLine)
            SyncLock _lock
                Dim available = CInt(Math.Min(_writePos, CLng(_ring.Length)))
                Dim take = Math.Min(count, available)
                Dim result As New List(Of LogLine)(take)
                For i = _writePos - take To _writePos - 1
                    Dim idx = CInt(((i Mod _ring.Length) + _ring.Length) Mod _ring.Length)
                    If _ring(idx) IsNot Nothing Then
                        result.Add(_ring(idx))
                    End If
                Next
                Return result
            End SyncLock
        End Function

    End Class

    ' ============================================================
    '  PluginOrphanDetector
    '
    '  Implements IOrphanDetector. Queries the database to find
    '  installations and instances whose GameId no longer has a
    '  loaded plugin (plugin was removed during hot-reload).
    ' ============================================================

    Public Class PluginOrphanDetector
        Implements IOrphanDetector

        Public Function GetOrphanedInstallationIds(gameId As String) As IReadOnlyList(Of String) Implements IOrphanDetector.GetOrphanedInstallationIds
            Using db = GsmDataExtensions.CreateDefaultContext()
                Return db.Installations.
                    Where(Function(i) i.GameId = gameId).
                    Select(Function(i) i.InstallationId).
                    ToList()
            End Using
        End Function

        Public Function GetOrphanedInstanceIds(gameId As String) As IReadOnlyList(Of String) Implements IOrphanDetector.GetOrphanedInstanceIds
            Using db = GsmDataExtensions.CreateDefaultContext()
                Return db.Instances.
                    Where(Function(i) i.GameId = gameId).
                    Select(Function(i) i.InstanceId).
                    ToList()
            End Using
        End Function

    End Class

End Namespace