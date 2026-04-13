Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Node.Api
Imports GSM.Manager
Imports GSM.Manager.Data

' ============================================================
'  InstanceManager — orchestrates instance lifecycle
'
'  Bridges the gap between the database (what's configured)
'  and the nodes (what's running). All instance operations
'  go through this class.
'
'  Responsibilities:
'    - Start/stop/restart instances via NodeHttpClient
'    - Track live state per instance
'    - Coordinate log streaming from nodes to manager buffer
'    - Resolve plugin config into plain DTOs for the node
' ============================================================

Namespace GSM.Manager.Core

    Public Class InstanceManager

        Private ReadOnly _clientFactory As NodeHttpClientFactory
        Private ReadOnly _pluginRegistry As PluginRegistry
        Private ReadOnly _credentialService As CredentialService
        Private ReadOnly _logger As ILogger(Of InstanceManager)
        Private ReadOnly _logParsers As New ConcurrentDictionary(Of String, ActiveLogParser)
        Private ReadOnly _logStreamCancellations As New ConcurrentDictionary(Of String, CancellationTokenSource)
        Private ReadOnly _liveStates As New ConcurrentDictionary(Of String, InstanceStatusResponse)

        Public Sub New(clientFactory As NodeHttpClientFactory,
                       pluginRegistry As PluginRegistry,
                       credentialService As CredentialService,
                       logger As ILogger(Of InstanceManager))
            _clientFactory = clientFactory
            _pluginRegistry = pluginRegistry
            _credentialService = credentialService
            _logger = logger
        End Sub

        ' ============================================================
        '  Instance lifecycle
        ' ============================================================

        ''' <summary>
        ''' Starts an instance on its node. Resolves plugin config,
        ''' builds launch arguments, sends StartInstanceRequest.
        ''' </summary>
        Public Async Function StartInstanceAsync(instanceId As String) As Task(Of Boolean)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                If instanceEntity Is Nothing Then
                    _logger.LogError("Instance {Id} not found", instanceId)
                    Return False
                End If

                Dim installEntity = db.Installations.Find(instanceEntity.InstallationId)
                If installEntity Is Nothing Then
                    _logger.LogError("Installation {Id} not found for instance {Inst}",
                                     instanceEntity.InstallationId, instanceId)
                    Return False
                End If

                Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                If nodeEntity Is Nothing Then
                    _logger.LogError("Node {Id} not found", installEntity.NodeId)
                    Return False
                End If

                ' Resolve plugin
                Dim plugin = _pluginRegistry.GetPlugin(instanceEntity.GameId)
                Dim customFields = DeserializeConfig(instanceEntity.ConfigJson)

                ' Build instance config
                Dim instanceConfig As New InstanceConfig With {
                    .InstanceId = instanceId,
                    .GameId = instanceEntity.GameId,
                    .DisplayName = instanceEntity.DisplayName,
                    .InstallationId = instanceEntity.InstallationId,
                    .WorkingDirectory = installEntity.InstallPath,
                    .CustomFields = customFields
                }

                ' Resolve launch arguments from plugin if available
                Dim launchArgs = ""
                Dim exePath = If(instanceEntity.ExeOverride, "")
                If plugin IsNot Nothing Then
                    Try
                        launchArgs = plugin.BuildLaunchArguments(instanceConfig)
                    Catch ex As Exception
                        _logger.LogWarning(ex, "Plugin failed to build launch arguments for {Id}", instanceId)
                    End Try
                End If
                If String.IsNullOrEmpty(exePath) Then
                    exePath = If(instanceConfig.ExePath, "")
                End If

                ' Get RCON settings from custom fields
                Dim rconPort As Integer? = Nothing
                Dim rconPassword = ""
                Dim rconProtocol = GSM.Plugin.RconProtocol.SourceRcon
                If plugin IsNot Nothing Then
                    Dim rp = plugin.GetRconProtocol()
                    If rp.HasValue Then rconProtocol = rp.Value
                End If
                If customFields.ContainsKey("RconPort") Then
                    Dim portVal As Integer = 0
                    If Integer.TryParse(customFields("RconPort"), portVal) Then
                        rconPort = portVal
                    End If
                End If
                If customFields.ContainsKey("RconPassword") Then
                    rconPassword = customFields("RconPassword")
                End If

                ' Build request
                Dim request As New StartInstanceRequest With {
                    .InstanceId = instanceId,
                    .ExePath = exePath,
                    .Arguments = launchArgs,
                    .WorkingDirectory = installEntity.InstallPath,
                    .EnvironmentVars = New Dictionary(Of String, String),
                    .CrashPolicy = CrashRestartPolicy.RestartWithBackoff,
                    .MaxCrashCount = If(instanceConfig.MaxCrashCount > 0, instanceConfig.MaxCrashCount, 5),
                    .CrashWindowMinutes = If(instanceConfig.CrashWindowMinutes > 0, instanceConfig.CrashWindowMinutes, 60),
                    .RconPort = rconPort,
                    .RconPassword = rconPassword,
                    .RconProtocol = rconProtocol
                }

                ' Send to node
                Dim client = _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)

                Try
                    Dim result = Await client.StartInstanceAsync(request, CancellationToken.None)
                    _liveStates(instanceId) = result
                    _logger.LogInformation("Started instance {Id} on node {Node}",
                                           instanceId, nodeEntity.DisplayName)

                    ' Start log streaming
                    StartLogStream(instanceId, client)

                    Return True
                Catch ex As Exception
                    _logger.LogError(ex, "Failed to start instance {Id}", instanceId)
                    Return False
                End Try
            End Using
        End Function

        ''' <summary>
        ''' Stops an instance on its node.
        ''' </summary>
        Public Async Function StopInstanceAsync(instanceId As String,
                                                Optional gracefulTimeoutMs As Integer = 10000) As Task(Of Boolean)
            ' Stop log streaming first
            StopLogStream(instanceId)

            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return False

            Try
                Dim request As New StopInstanceRequest With {
                    .InstanceId = instanceId,
                    .GracefulTimeoutMs = gracefulTimeoutMs
                }
                Dim result = Await client.StopInstanceAsync(request, CancellationToken.None)
                _liveStates(instanceId) = result
                _logger.LogInformation("Stopped instance {Id}", instanceId)
                Return True
            Catch ex As Exception
                _logger.LogError(ex, "Failed to stop instance {Id}", instanceId)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Restarts an instance (stop then start).
        ''' </summary>
        Public Async Function RestartInstanceAsync(instanceId As String) As Task(Of Boolean)
            Dim stopped = Await StopInstanceAsync(instanceId)
            If Not stopped Then Return False
            Await Task.Delay(2000)
            Return Await StartInstanceAsync(instanceId)
        End Function

        ''' <summary>
        ''' Returns the last known live state for an instance.
        ''' </summary>
        Public Function GetLiveState(instanceId As String) As InstanceStatusResponse
            Dim result As InstanceStatusResponse = Nothing
            _liveStates.TryGetValue(instanceId, result)
            Return result
        End Function

        ''' <summary>
        ''' Polls the node for the current state of an instance.
        ''' </summary>
        Public Async Function RefreshInstanceStateAsync(instanceId As String) As Task(Of InstanceStatusResponse)
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return Nothing

            Try
                Dim result = Await client.GetInstanceStatusAsync(instanceId, CancellationToken.None)
                _liveStates(instanceId) = result
                Return result
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to refresh state for {Id}", instanceId)
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Sends an RCON command to an instance.
        ''' </summary>
        Public Async Function SendRconCommandAsync(instanceId As String,
                                                    command As String) As Task(Of String)
            Dim client = GetClientForInstance(instanceId)
            If client Is Nothing Then Return "No node client available"

            Try
                Dim request As New RconCommandRequest With {
                    .InstanceId = instanceId,
                    .Command = command
                }
                Dim result = Await client.SendRconCommandAsync(request, CancellationToken.None)
                If result.Success Then
                    Return result.Response
                End If
                Return $"RCON error: {result.ErrorMessage}"
            Catch ex As Exception
                Return $"RCON exception: {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Gets the player count for an instance (via RCON or node query).
        ''' Returns 0 if unavailable.
        ''' </summary>
        Public Function GetPlayerCountAsync(instanceId As String) As Task(Of Integer)
            ' For now, return 0 — proper implementation requires
            ' game-specific RCON commands via the plugin
            Return Task.FromResult(0)
        End Function

        ''' <summary>
        ''' Returns all instance IDs for an installation.
        ''' </summary>
        Public Function GetInstanceIdsForInstallation(installationId As String) As IReadOnlyList(Of String)
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Return db.Instances.
                    Where(Function(i) i.InstallationId = installationId).
                    Select(Function(i) i.InstanceId).
                    ToList()
            End Using
        End Function

        ' ============================================================
        '  Log streaming
        ' ============================================================

        Private Sub StartLogStream(instanceId As String, client As INodeClient)
            Dim cts As New CancellationTokenSource()
            _logStreamCancellations(instanceId) = cts

            ' Create a log parser for this instance's game
            Dim gameId = GetGameIdForInstance(instanceId)
            Dim parser As ILogParser = Nothing
            If gameId IsNot Nothing Then
                parser = _pluginRegistry.CreateParser(gameId)
            End If

            If parser IsNot Nothing Then
                _logParsers(instanceId) = New ActiveLogParser With {
                    .Parser = parser,
                    .InstanceId = instanceId
                }
            End If

            ' Stream in background
            Task.Run(Function() StreamLogsInBackgroundAsync(instanceId, client, parser, cts.Token))
        End Sub

        Private Async Function StreamLogsInBackgroundAsync(instanceId As String,
                                                            client As INodeClient,
                                                            parser As ILogParser,
                                                            cancellation As CancellationToken) As Task
            Try
                Await client.StreamLogsAsync(instanceId,
                    Sub(line)
                        ' Store in manager-side buffer
                        Dim logStore = ManagerProgram.Services.GetService(Of ManagerRingBufferStore)()
                        If logStore IsNot Nothing Then
                            logStore.Append(instanceId, line)
                        End If

                        ' Run through parser if available
                        If parser IsNot Nothing Then
                            Try
                                Dim parsed = parser.ParseLine(line)
                                ' Could fire events here for notifications
                            Catch
                            End Try
                        End If
                    End Sub, cancellation)
            Catch ex As OperationCanceledException
                ' Normal
            Catch ex As Exception
                _logger.LogWarning(ex, "Log stream ended for {Id}", instanceId)
            End Try
        End Function

        Private Sub StopLogStream(instanceId As String)
            Dim cts As CancellationTokenSource = Nothing
            If _logStreamCancellations.TryRemove(instanceId, cts) Then
                cts.Cancel()
                cts.Dispose()
            End If
            Dim removedParser As ActiveLogParser = Nothing
            _logParsers.TryRemove(instanceId, removedParser)
        End Sub

        ' ============================================================
        '  Helpers
        ' ============================================================

        Private Function GetClientForInstance(instanceId As String) As INodeClient
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                If instanceEntity Is Nothing Then Return Nothing

                Dim installEntity = db.Installations.Find(instanceEntity.InstallationId)
                If installEntity Is Nothing Then Return Nothing

                Dim nodeEntity = db.Nodes.Find(installEntity.NodeId)
                If nodeEntity Is Nothing Then Return Nothing

                Return _clientFactory.GetClient(
                    nodeEntity.NodeId, nodeEntity.HostAddress,
                    nodeEntity.Port, nodeEntity.AuthToken)
            End Using
        End Function

        Private Function GetGameIdForInstance(instanceId As String) As String
            Using scope = ManagerProgram.Services.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                Dim instanceEntity = db.Instances.Find(instanceId)
                Return instanceEntity?.GameId
            End Using
        End Function

        Private Shared Function DeserializeConfig(json As String) As Dictionary(Of String, String)
            If String.IsNullOrEmpty(json) Then Return New Dictionary(Of String, String)
            Try
                Return JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
            Catch
                Return New Dictionary(Of String, String)
            End Try
        End Function

    End Class

    ' ============================================================
    '  ActiveLogParser — tracks a running parser per instance
    ' ============================================================

    Public Class ActiveLogParser
        Public Property Parser As ILogParser
        Public Property InstanceId As String
    End Class

End Namespace
