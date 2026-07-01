Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
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

        ' ============================================================
        '  Phase 5m-2c — subsystem start controller
        '
        '  Safe mode boots with the risky subsystems NOT started (see
        '  the gated blocks in Main). This lets the safe-mode "Re-enable
        '  Features" panel turn them back on one at a time at runtime,
        '  so an operator can fix a bad rule / plugin and test just that
        '  subsystem without leaving the safe harbour. Re-enable only —
        '  to turn something back OFF, restart safe mode.
        '
        '  Used only from the safe-mode panel. Normal startup keeps its
        '  own inline start blocks in Main (which carry extra bits like
        '  the plugin-summary status line), so this controller's
        '  started-set stays empty on a normal launch — fine, since the
        '  panel is shown only in safe mode.
        ' ============================================================

        Public Enum ManagerSubsystem
            Plugins
            NodePolling
            Notifications
            Automation
            VersionCheck
            ChatPruner
        End Enum

        Private ReadOnly _startedSubsystems As New HashSet(Of ManagerSubsystem)()

        Public Function IsSubsystemStarted(target As ManagerSubsystem) As Boolean
            Return _startedSubsystems.Contains(target)
        End Function

        Public Function SubsystemDisplayName(target As ManagerSubsystem) As String
            Select Case target
                Case ManagerSubsystem.Plugins : Return "Plugins (compile + load)"
                Case ManagerSubsystem.NodePolling : Return "Node polling + log streams"
                Case ManagerSubsystem.Notifications : Return "Discord notifications + bot"
                Case ManagerSubsystem.Automation : Return "Automation engine"
                Case ManagerSubsystem.VersionCheck : Return "Version checking"
                Case ManagerSubsystem.ChatPruner : Return "Chat retention pruner"
                Case Else : Return target.ToString()
            End Select
        End Function

        ''' <summary>
        ''' Starts a subsystem on demand (the safe-mode re-enable path).
        ''' Idempotent — a no-op if already started. Returns Nothing on
        ''' success or an error message on failure. VersionCheck pulls
        ''' Automation up first, since it raises events into the engine.
        ''' </summary>
        Public Function StartSubsystem(target As ManagerSubsystem) As String
            If _startedSubsystems.Contains(target) Then Return Nothing
            If Services Is Nothing Then Return "Services not initialised."

            Try
                Select Case target
                    Case ManagerSubsystem.Plugins
                        Dim registry = Services.GetRequiredService(Of PluginRegistry)()
                        Dim orphanDetector = Services.GetService(Of PluginOrphanDetector)()
                        registry.ReloadAll(orphanDetector)

                    Case ManagerSubsystem.NodePolling
                        Dim mgr = Services.GetService(Of InstanceManager)()
                        If mgr IsNot Nothing Then
                            Dim m = mgr
                            Task.Run(Async Function()
                                         Await m.ReconnectLogStreamsAsync()
                                     End Function)
                            mgr.StartBackgroundPolling()
                        End If

                    Case ManagerSubsystem.Notifications
                        Dim notifications = Services.GetRequiredService(Of NotificationService)()
                        Dim webhook = Services.GetRequiredService(Of DiscordWebhookPlugin)()
                        Dim bot = Services.GetRequiredService(Of DiscordBotPlugin)()
                        notifications.RegisterPluginAsync(webhook, CancellationToken.None).GetAwaiter().GetResult()
                        notifications.RegisterPluginAsync(bot, CancellationToken.None).GetAwaiter().GetResult()

                    Case ManagerSubsystem.Automation
                        Dim engine = Services.GetService(Of AutomationEngine)()
                        engine?.Start()

                    Case ManagerSubsystem.VersionCheck
                        ' Depends on the automation engine (raises
                        ' version-mismatch events into it). Pull it up
                        ' first so those events aren't silently dropped.
                        Dim depErr = StartSubsystem(ManagerSubsystem.Automation)
                        If depErr IsNot Nothing Then Return depErr
                        Dim versionCheck = Services.GetService(Of VersionCheckService)()
                        versionCheck?.Start()

                    Case ManagerSubsystem.ChatPruner
                        Dim pruner = Services.GetService(Of ChatRetentionPruner)()
                        pruner?.Start()
                End Select
            Catch ex As Exception
                Return ex.Message
            End Try

            _startedSubsystems.Add(target)
            Return Nothing
        End Function

        ' Phase 5m-3 — single-instance + watchdog integration.
        ' The mutex name is shared with GSM.Watchdog (hardcoded in both;
        ' the projects are intentionally decoupled). A second Manager
        ' instance signals the activation event so the primary comes
        ' forward, then exits — with the deferred code when launched by
        ' the watchdog, so the watchdog monitors the existing instance
        ' rather than treating the quick exit as a crash.
        Private Const SingleInstanceMutexName As String = "PowerGSM.Manager.SingleInstance"
        Private Const ActivateEventName As String = "PowerGSM.Manager.Activate"
        Private Const EnvWatched As String = "POWERGSM_WATCHDOG"

        ' Manager exit-code contract (must match GSM.Watchdog):
        Private Const ExitDeferred As Integer = 10
        Private Const ExitRelaunchNormal As Integer = 20
        Private Const ExitRelaunchSafe As Integer = 21

        Private _singleInstanceMutex As Mutex
        Private _activateEvent As EventWaitHandle

        <STAThread>
        Sub Main()

            Application.SetHighDpiMode(HighDpiMode.SystemAware)
            Application.EnableVisualStyles()
            Application.SetCompatibleTextRenderingDefault(False)

            ' ---- Phase 5m-3: single instance ----
            ' One Manager per session. A second launch (manual
            ' double-click, Startup shortcut, or a watchdog spawn racing
            ' a manual launch) signals the running instance to come
            ' forward and bows out. When the watchdog launched us
            ' (POWERGSM_WATCHDOG=1) the bow-out uses the deferred exit
            ' code so the watchdog monitors the existing Manager rather
            ' than reading the quick exit as a crash.
            Dim watched As Boolean =
                String.Equals(Environment.GetEnvironmentVariable(EnvWatched), "1", StringComparison.Ordinal)
            Dim isPrimaryInstance As Boolean = False
            _singleInstanceMutex = New Mutex(True, SingleInstanceMutexName, isPrimaryInstance)
            If Not isPrimaryInstance Then
                Try
                    Using sig = EventWaitHandle.OpenExisting(ActivateEventName)
                        sig.Set()
                    End Using
                Catch
                    ' Couldn't signal (no listener yet) — bow out anyway.
                End Try
                Environment.Exit(If(watched, ExitDeferred, 0))
            End If
            ' Primary instance — create the activation event a later
            ' would-be instance signals. The listener starts once
            ' MainForm exists (below).
            Try
                _activateEvent = New EventWaitHandle(False, EventResetMode.AutoReset, ActivateEventName)
            Catch
            End Try

            ' ---- Phase 5m-2: safe mode + crash marker ----
            '
            ' Safe mode brings the window up bare — no plugin compile,
            ' no automation, no Discord, no node polling — so a load-
            ' time culprit (a plugin that throws during Roslyn compile,
            ' an automation rule that crashes on first fire, a wedged
            ' Discord connect) can be bypassed to get a working UI for
            ' repair. Entered explicitly via the --safe-mode argument
            ' (the in-app "Restart in Safe Mode" relaunches with it),
            ' or offered automatically when the previous session didn't
            ' shut down cleanly.
            Dim explicitSafe As Boolean = False
            Dim postUpdateVersion As String = Nothing
            Dim cmdLineArgs = Environment.GetCommandLineArgs()
            For i = 0 To cmdLineArgs.Length - 1
                Dim arg = cmdLineArgs(i)
                If String.Equals(arg, "--safe-mode", StringComparison.OrdinalIgnoreCase) Then
                    explicitSafe = True
                ElseIf String.Equals(arg, "--post-update", StringComparison.OrdinalIgnoreCase) Then
                    If i + 1 < cmdLineArgs.Length Then postUpdateVersion = cmdLineArgs(i + 1)
                End If
            Next

            ' Marker file dropped at startup and cleared on a clean
            ' Application.Run return. Still present at the next launch =>
            ' the previous session died without reaching clean shutdown
            ' (crash, kill, power loss).
            Dim crashMarkerPath = IO.Path.Combine(AppContext.BaseDirectory, ".manager-running")
            Dim previousRunUnclean As Boolean = False
            Try
                previousRunUnclean = IO.File.Exists(crashMarkerPath)
            Catch
            End Try

            Dim safeMode As Boolean = explicitSafe
            If Not safeMode AndAlso previousRunUnclean Then
                Dim resp = MessageBox.Show(
                    "PowerGSM didn't shut down cleanly last time." & vbCrLf & vbCrLf &
                    "Start in Safe Mode? This opens the Manager without loading plugins, " &
                    "starting automation, connecting Discord, or polling nodes — so you can " &
                    "fix whatever caused the problem." & vbCrLf & vbCrLf &
                    "Choose No to start normally.",
                    "PowerGSM — Safe Mode",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                safeMode = (resp = DialogResult.Yes)
            End If

            ' (Re)drop the marker for this session.
            Try
                IO.File.WriteAllText(crashMarkerPath,
                    $"{DateTime.UtcNow:o} pid={Environment.ProcessId} safeMode={safeMode}")
            Catch
            End Try

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
            '
            ' Resolve the DB path against AppContext.BaseDirectory (the
            ' binary's folder) instead of letting the relative
            ' "Data Source=gsm.db" resolve to the process working
            ' directory. For double-click / VS-debug launches the
            ' working dir happens to equal the binary dir, so the DB
            ' landed next to the binary by coincidence — but a launch
            ' that sets a different working dir (a shortcut's "Start in",
            ' or a Task Scheduler entry — both arrive with the Phase 5m
            ' watchdog) would otherwise create a fresh empty DB in the
            ' wrong place and look like total data loss. Anchoring to
            ' BaseDirectory makes it robust regardless of launch method
            ' and points at the same file existing deployments already
            ' have alongside the binary. (The design-time
            ' GsmDbContextFactory keeps its own relative path — that's
            ' a dev-tooling concern for Add-Migration, not runtime.)
            Dim dbPath = IO.Path.Combine(AppContext.BaseDirectory, "gsm.db")
            serviceCollection.AddDbContext(Of GsmDbContext)(
                Sub(opts)
                    opts.UseSqlite($"Data Source={dbPath}")
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
            serviceCollection.AddSingleton(Of SharedConfigService)()
            serviceCollection.AddSingleton(Of IdentityResolver)()
            serviceCollection.AddSingleton(Of PluginRegistry)()
            serviceCollection.AddSingleton(Of InstanceManager)()
            serviceCollection.AddSingleton(Of InstallationManager)()
            serviceCollection.AddSingleton(Of NotificationService)()
            serviceCollection.AddSingleton(Of AutomationEngine)()
            serviceCollection.AddSingleton(Of ChatRetentionPruner)()
            serviceCollection.AddSingleton(Of VersionCheckService)()
            serviceCollection.AddSingleton(Of GitHubReleaseChecker)()
            serviceCollection.AddSingleton(Of UpdateOrchestrator)()
            serviceCollection.AddSingleton(Of NodeReleaseSource)()
            serviceCollection.AddSingleton(Of PluginCompatibilityChecker)()
            serviceCollection.AddSingleton(Of PluginCatalogService)()
            serviceCollection.AddSingleton(Of PluginStageService)()
            serviceCollection.AddSingleton(Of WebSessionStore)()
            serviceCollection.AddSingleton(Of UtilityPluginHost)()
            serviceCollection.AddSingleton(Of PortalImportService)()
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

            ' Phase 6-2 — ensure the official plugin source row exists.
            ' Idempotent; safe to run on every startup after Migrate().
            Try
                Services.GetRequiredService(Of PluginCatalogService)().EnsureOfficialSeeded()
            Catch
                ' Seeding failure must never block startup.
            End Try

            ' Phase 5g-2d — hydrate the IdentityResolver cache from
            ' recent PlayerActivity rows so the first Enrich call
            ' after startup has resolved-identity data to work with.
            ' Best-effort: a hydration failure (DB unreachable, table
            ' missing because migration somehow didn't apply, etc.)
            ' logs a warning inside HydrateAsync and lets the resolver
            ' fall through to live-observation-only mode. Blocks
            ' startup briefly (typically <100ms for the 5000-row /
            ' 30-day default window) so InstanceManager and friends
            ' see a populated cache from their first Observe.
            Try
                Dim resolver = Services.GetRequiredService(Of IdentityResolver)()
                resolver.HydrateAsync().GetAwaiter().GetResult()
            Catch ex As Exception
                Services.GetRequiredService(Of ILoggerFactory)().
                    CreateLogger("Startup").
                    LogWarning(ex, "IdentityResolver.HydrateAsync threw at startup")
            End Try

            ' Load plugins on startup (skipped in safe mode — a plugin
            ' that throws or hangs during Roslyn compile is a prime
            ' reason to need safe mode in the first place).
            Dim registry = Services.GetRequiredService(Of PluginRegistry)()
            Dim orphanDetector = Services.GetRequiredService(Of PluginOrphanDetector)()

            ' Phase 7-2 — materialise the utility-plugin host BEFORE the
            ' first ReloadAll so its Reloaded subscription catches the
            ' startup load (DI singletons are lazy; an unreferenced host
            ' would never construct, never subscribe, and utility
            ' plugins would load but never initialise).
            Services.GetRequiredService(Of UtilityPluginHost)()

            Dim pluginSummary As PluginReloadSummary = Nothing
            If Not safeMode Then
                pluginSummary = registry.ReloadAll(orphanDetector)
                If pluginSummary.CompilationErrors.Count > 0 Then
                    Dim errMsg = $"{pluginSummary.CompilationErrors.Count} plugin compilation error(s):" & vbCrLf
                    For Each compErr In pluginSummary.CompilationErrors
                        errMsg &= $"  {compErr.FileName}: {compErr.Message}" & vbCrLf
                    Next
                    MessageBox.Show(errMsg, "Plugin Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                End If
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
            If Not safeMode Then
                Try
                    notifications.RegisterPluginAsync(
                        discordPlugin, CancellationToken.None).
                        GetAwaiter().GetResult()
                Catch ex As Exception
                    ' Never fatal — Manager runs with or without Discord.
                    startupLogger.LogWarning(ex, "Failed to register Discord plugin at startup")
                End Try
            End If

            ' Phase 5d-1 — register the Discord bot plugin. Same
            ' pattern as the webhook plugin: best-effort, a failure
            ' (bad token, Discord unreachable, missing config) logs
            ' a warning and leaves the Manager running.
            Dim discordBotPlugin = Services.GetRequiredService(Of DiscordBotPlugin)()
            If Not safeMode Then
                Try
                    notifications.RegisterPluginAsync(
                        discordBotPlugin, CancellationToken.None).
                        GetAwaiter().GetResult()
                Catch ex As Exception
                    startupLogger.LogWarning(ex, "Failed to register Discord bot plugin at startup")
                End Try
            End If

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

            If mgr IsNot Nothing AndAlso Not safeMode Then
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
            If pruner IsNot Nothing AndAlso Not safeMode Then
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
            If Not safeMode Then
                Try
                    engine?.Start()
                Catch ex As Exception
                    startupLogger.LogWarning(ex, "AutomationEngine.Start threw at startup")
                End Try
            End If

            ' VersionCheckService starts AFTER the engine because it
            ' calls AutomationEngine.RaiseVersionMismatchAsync when
            ' it detects mismatches. If the engine isn't started
            ' first, those calls would no-op silently.
            If versionCheck IsNot Nothing AndAlso Not safeMode Then
                Try
                    versionCheck.Start()
                Catch ex As Exception
                    startupLogger.LogWarning(ex, "VersionCheckService.Start threw at startup")
                End Try
            End If

            ' Phase 5l-1 — self-update detection. Background poll of
            ' GitHub Releases. Read-only and independent of the
            ' automation engine, so ordering relative to it doesn't
            ' matter; gated out of safe mode like the other background
            ' services.
            If Not safeMode Then
                Try
                    Dim releaseChecker = Services.GetService(Of GitHubReleaseChecker)()
                    releaseChecker?.Start()
                Catch ex As Exception
                    startupLogger.LogWarning(ex, "GitHubReleaseChecker.Start threw at startup")
                End Try
            End If

            ' Launch main form
            Dim mainForm As New UI.MainForm(safeMode)
            If safeMode Then
                mainForm.SetStatus("SAFE MODE — plugins, automation, Discord, and node polling are disabled")
            Else
                mainForm.SetStatus($"Plugins: {pluginSummary.LoadedPlugins.Count} loaded, {pluginSummary.CompilationErrors.Count} errors")
            End If

            ' Phase 5m-3 — listen for a second instance asking us to come
            ' forward (it Set()s the named activation event); restore +
            ' activate the window. Background thread; the UI work is
            ' marshalled onto the UI thread.
            If _activateEvent IsNot Nothing Then
                Dim activatorForm = mainForm
                Dim activator As New Thread(Sub() ActivationListener(activatorForm))
                activator.IsBackground = True
                activator.Start()
            End If

            ' ---- Phase 5l-3: self-update post-apply handling ----
            ' Surface a failed apply (apply.cmd's fail path leaves a log
            ' even when it never relaunched us), and on a successful
            ' --post-update relaunch, log + clean up the staging folder.
            Try
                Dim orchPost = Services.GetService(Of UpdateOrchestrator)()
                If orchPost IsNot Nothing Then
                    Dim applyErr = orchPost.TakeApplyError()
                    If Not String.IsNullOrWhiteSpace(applyErr) Then
                        orchPost.RecordFailedApply(applyErr)
                        MessageBox.Show(
                            "The last update could not be applied. The previous binaries were left in place or can be restored." & Environment.NewLine & Environment.NewLine &
                            applyErr.Trim() & Environment.NewLine & Environment.NewLine &
                            "If PowerGSM isn't working, restore GSM.Manager.exe and GSM.Contracts.dll from the '.updates\rollback' folder next to the app.",
                            "Update not applied", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    End If
                    If Not String.IsNullOrEmpty(postUpdateVersion) Then
                        orchPost.CompletePostUpdate(postUpdateVersion)
                    End If
                End If
            Catch
                ' Post-update bookkeeping must never block startup.
            End Try

            Application.Run(mainForm)

            ' Clean exit — the window closed normally, so clear the
            ' crash marker. A crash/kill mid-session never reaches here,
            ' leaving the marker for the next launch to detect.
            Try
                IO.File.Delete(crashMarkerPath)
            Catch
            End Try

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
                Services.GetService(Of GitHubReleaseChecker)()?.StopAsync().GetAwaiter().GetResult()
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

            ' ---- Phase 5l-3: apply a staged update on exit ----
            ' If the user committed to an update, spawn the detached
            ' apply.cmd and return WITHOUT setting a relaunch code: a
            ' clean exit (0) makes the watchdog stand down (no relaunch
            ' race), and apply.cmd performs the binary swap + relaunch.
            Try
                Dim orchApply = Services.GetService(Of UpdateOrchestrator)()
                If orchApply IsNot Nothing AndAlso orchApply.HasPendingApply Then
                    orchApply.LaunchPendingApply()
                    Return
                End If
            Catch
                ' Couldn't spawn the updater — fall through to a normal
                ' exit; nothing was swapped, the install is intact.
            End Try

            ' Phase 5m-2b / 5m-3 — honour an in-app restart request.
            ' Done after clean shutdown + marker clear so the new
            ' instance starts from a clean slate (no stale crash marker,
            ' no contention with the outgoing process).
            '
            ' Under the watchdog (POWERGSM_WATCHDOG=1) we do NOT
            ' self-spawn — that would leave an unwatched Manager while
            ' the watchdog stood down on our exit. Instead we exit with
            ' the relaunch code and let the watchdog relaunch us (with or
            ' without --safe-mode), keeping the replacement supervised.
            Try
                Dim relaunch = mainForm.RequestedRelaunch
                If relaunch <> UI.RelaunchRequest.None Then
                    If watched Then
                        Environment.ExitCode = If(relaunch = UI.RelaunchRequest.SafeMode,
                                                  ExitRelaunchSafe, ExitRelaunchNormal)
                    Else
                        ' Release our single-instance hold BEFORE spawning
                        ' the replacement — otherwise the new instance
                        ' sees the mutex still held by this (not-yet-
                        ' exited) process and bows out as a duplicate,
                        ' leaving NO Manager running. (The watched branch
                        ' above has no such race: the watchdog relaunches
                        ' only after this process has fully exited.)
                        Try
                            _singleInstanceMutex?.ReleaseMutex()
                            _singleInstanceMutex?.Dispose()
                            _singleInstanceMutex = Nothing
                        Catch
                        End Try
                        Dim exePath = Environment.ProcessPath
                        If String.IsNullOrEmpty(exePath) Then exePath = Application.ExecutablePath
                        Dim psi As New ProcessStartInfo() With {
                            .FileName = exePath,
                            .UseShellExecute = True
                        }
                        If relaunch = UI.RelaunchRequest.SafeMode Then psi.Arguments = "--safe-mode"
                        Process.Start(psi)
                    End If
                End If
            Catch
            End Try

        End Sub

        ''' <summary>
        ''' Phase 5m-3 — background listener: when a second Manager
        ''' instance signals the activation event, bring this (the
        ''' primary) window forward. AutoReset, so each signal fires
        ''' once; the loop exits when the handle is disposed at shutdown.
        ''' </summary>
        Private Sub ActivationListener(form As UI.MainForm)
            While True
                Try
                    _activateEvent.WaitOne()
                Catch
                    Exit While   ' event disposed / shutting down
                End Try
                Try
                    If form.IsHandleCreated Then
                        form.BeginInvoke(New Action(Sub() BringToFront(form)))
                    End If
                Catch
                    ' Transient (e.g. handle not ready) — keep listening.
                End Try
            End While
        End Sub

        Private Sub BringToFront(form As UI.MainForm)
            Try
                If form.WindowState = FormWindowState.Minimized Then
                    form.WindowState = FormWindowState.Normal
                End If
                form.Show()
                form.Activate()
                form.BringToFront()
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
    '  Orphan reconciliation result types (Phase 5m-2e)
    ' ============================================================

    ''' <summary>
    ''' Phase 5m-2e — a referenced-but-unloaded installation or
    ''' instance: its GameId has no loaded plugin.
    ''' </summary>
    Public Class OrphanedRef
        Public Property Id As String
        Public Property DisplayName As String
        Public Property GameId As String
    End Class

    ''' <summary>
    ''' Phase 5m-2e — result of reconciling the DB's referenced
    ''' GameIds against the set of loaded plugins.
    ''' </summary>
    Public Class OrphanReport
        Public Property Installations As New List(Of OrphanedRef)
        Public Property Instances As New List(Of OrphanedRef)

        Public ReadOnly Property HasAny As Boolean
            Get
                Return Installations.Count > 0 OrElse Instances.Count > 0
            End Get
        End Property

        ''' <summary>Distinct missing GameIds across both lists.</summary>
        Public ReadOnly Property MissingGameIds As List(Of String)
            Get
                Return Installations.Select(Function(r) r.GameId).
                    Concat(Instances.Select(Function(r) r.GameId)).
                    Where(Function(g) Not String.IsNullOrEmpty(g)).
                    Distinct(StringComparer.OrdinalIgnoreCase).
                    OrderBy(Function(g) g, StringComparer.OrdinalIgnoreCase).
                    ToList()
            End Get
        End Property
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

        ''' <summary>
        ''' Phase 5m-2e — reconcile every installation/instance in the
        ''' DB against the set of loaded plugin GameIds. Anything whose
        ''' GameId has no loaded plugin is orphaned. Unlike the
        ''' diff-based interface methods above, this catches orphans at
        ''' startup and across sessions (a plugin deleted between runs),
        ''' not just plugins removed during an in-session hot-reload.
        ''' </summary>
        Public Function BuildOrphanReport(loadedGameIds As IEnumerable(Of String)) As OrphanReport
            Dim loaded As New HashSet(Of String)(
                If(loadedGameIds, Enumerable.Empty(Of String)()),
                StringComparer.OrdinalIgnoreCase)
            Dim report As New OrphanReport()
            Using db = GsmDataExtensions.CreateDefaultContext()
                For Each r In db.Installations.
                        Select(Function(i) New With {i.InstallationId, i.DisplayName, i.GameId}).
                        ToList()
                    If Not loaded.Contains(If(r.GameId, "")) Then
                        report.Installations.Add(New OrphanedRef With {
                            .Id = r.InstallationId, .DisplayName = r.DisplayName, .GameId = r.GameId})
                    End If
                Next
                For Each r In db.Instances.
                        Select(Function(i) New With {i.InstanceId, i.DisplayName, i.GameId}).
                        ToList()
                    If Not loaded.Contains(If(r.GameId, "")) Then
                        report.Instances.Add(New OrphanedRef With {
                            .Id = r.InstanceId, .DisplayName = r.DisplayName, .GameId = r.GameId})
                    End If
                Next
            End Using
            Return report
        End Function

    End Class

End Namespace