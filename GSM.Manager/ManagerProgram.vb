Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Windows.Forms
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Manager.Core
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
            serviceCollection.AddLogging(Sub(cfg)
                                             cfg.AddConsole()
                                             cfg.SetMinimumLevel(LogLevel.Information)
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

            ' ---- Phase 4 Core services ----
            serviceCollection.AddSingleton(Of NodeHttpClientFactory)()
            serviceCollection.AddSingleton(Of CredentialService)()
            serviceCollection.AddSingleton(Of PluginRegistry)()
            serviceCollection.AddSingleton(Of InstanceManager)()
            serviceCollection.AddSingleton(Of InstallationManager)()
            serviceCollection.AddSingleton(Of NotificationService)()
            serviceCollection.AddSingleton(Of AutomationEngine)()

            ' Build provider
            Services = serviceCollection.BuildServiceProvider()

            ' Ensure database exists
            Using scope = Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                db.Database.EnsureCreated()
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

            ' Launch main form
            Dim mainForm As New UI.MainForm()
            mainForm.SetStatus($"Plugins: {pluginSummary.LoadedPlugins.Count} loaded, {pluginSummary.CompilationErrors.Count} errors")
            Application.Run(mainForm)

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