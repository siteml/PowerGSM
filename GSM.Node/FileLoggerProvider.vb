Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Text
Imports Microsoft.Extensions.Logging

' ============================================================
'  FileLoggerProvider — minimal dependency-free file sink
'
'  Node-side counterpart to GSM.Manager.FileLoggerProvider. The
'  code is intentionally duplicated rather than shared via a
'  third project: GSM.Node and GSM.Manager don't reference each
'  other (only via the GSM.Contracts DTOs), and pulling 100 lines
'  of logging plumbing into Contracts would mean Contracts takes
'  a Microsoft.Extensions.Logging.Abstractions reference for
'  a single-purpose helper neither side touches via Contracts.
'  Keeping the two copies independent is cheaper than the
'  abstraction.
'
'  This provider writes one UTF-8 text file per day under
'  {DataDirectory}/logs/node-YYYY-MM-DD.log. Writes are
'  serialised through a single lock — throughput is fine for
'  diagnostic logging, and the simplicity means we don't
'  accumulate unflushed buffers on unclean shutdown.
'
'  The default ASP.NET Core logging that WebApplication.CreateBuilder
'  wires up writes to the host console. The node typically runs
'  visible (cmd window or service log redirect), so console
'  output is useful — but ephemeral. This file sink lets us
'  diagnose intermittent issues that didn't have someone watching
'  the console at the time.
' ============================================================

Namespace GSM.Node

    Public NotInheritable Class FileLoggerProvider
        Implements ILoggerProvider

        Private ReadOnly _directory As String
        Private ReadOnly _minLevel As LogLevel
        Private ReadOnly _filenamePrefix As String
        Private ReadOnly _loggers As New ConcurrentDictionary(Of String, FileLogger)
        Private ReadOnly _writeLock As New Object()
        Private _currentDate As DateTime
        Private _currentWriter As StreamWriter
        Private _disposed As Boolean

        Public Sub New(directory As String,
                       Optional minLevel As LogLevel = LogLevel.Information,
                       Optional filenamePrefix As String = "node-")
            _directory = directory
            _minLevel = minLevel
            _filenamePrefix = If(filenamePrefix, "node-")
            Try
                IO.Directory.CreateDirectory(_directory)
            Catch
                ' If the directory can't be created we'll fail per-write
                ' too; don't take down startup over it.
            End Try
        End Sub

        Public Function CreateLogger(categoryName As String) As ILogger _
            Implements ILoggerProvider.CreateLogger
            Return _loggers.GetOrAdd(categoryName,
                Function(n) New FileLogger(Me, n))
        End Function

        Friend Function IsLevelEnabled(level As LogLevel) As Boolean
            Return level >= _minLevel AndAlso level <> LogLevel.None
        End Function

        Friend Sub WriteLine(level As LogLevel, category As String,
                              message As String, ex As Exception)
            If _disposed Then Return

            Dim now = DateTime.Now
            Dim sb As New StringBuilder(256)
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            sb.Append(" [")
            sb.Append(LevelTag(level))
            sb.Append("] ")
            sb.Append(category)
            sb.Append(": ")
            sb.Append(message)
            If ex IsNot Nothing Then
                sb.AppendLine()
                sb.Append(ex.ToString())
            End If

            SyncLock _writeLock
                Try
                    EnsureWriterForDate(now.Date)
                    If _currentWriter IsNot Nothing Then
                        _currentWriter.WriteLine(sb.ToString())
                        _currentWriter.Flush()
                    End If
                Catch
                    ' Logging must never throw from the caller's
                    ' perspective — swallow and move on.
                End Try
            End SyncLock
        End Sub

        ''' <summary>
        ''' Opens (or re-opens on day rollover) the writer for the
        ''' supplied date. Must be called under _writeLock.
        ''' </summary>
        Private Sub EnsureWriterForDate(d As DateTime)
            If _currentWriter IsNot Nothing AndAlso d = _currentDate Then Return

            Try
                _currentWriter?.Dispose()
            Catch
            End Try
            _currentWriter = Nothing

            Dim path = IO.Path.Combine(_directory, $"{_filenamePrefix}{d:yyyy-MM-dd}.log")
            Dim fs = New FileStream(path, FileMode.Append, FileAccess.Write,
                                    FileShare.Read Or FileShare.Delete)
            _currentWriter = New StreamWriter(fs, New UTF8Encoding(False))
            _currentDate = d
        End Sub

        Private Shared Function LevelTag(level As LogLevel) As String
            Select Case level
                Case LogLevel.Trace : Return "TRC"
                Case LogLevel.Debug : Return "DBG"
                Case LogLevel.Information : Return "INF"
                Case LogLevel.Warning : Return "WRN"
                Case LogLevel.Error : Return "ERR"
                Case LogLevel.Critical : Return "CRT"
                Case Else : Return level.ToString()
            End Select
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _disposed = True
            SyncLock _writeLock
                Try
                    _currentWriter?.Flush()
                    _currentWriter?.Dispose()
                Catch
                End Try
                _currentWriter = Nothing
            End SyncLock
        End Sub

        ''' <summary>
        ''' Deletes log files older than the retention window.
        ''' Called at startup so old files clean up without
        ''' needing a separate background task. The daily-rotation
        ''' file naming makes the age check trivial: parse the
        ''' date out of the filename and compare to today.
        '''
        ''' Files that don't match the expected
        ''' "&lt;prefix&gt;YYYY-MM-DD.log" pattern are left alone —
        ''' an operator might have renamed something for archival
        ''' and we shouldn't delete that. Files where the date
        ''' parse fails are similarly skipped.
        '''
        ''' Per-file failures are swallowed: a permission error
        ''' on one file shouldn't abort retention of the rest.
        ''' </summary>
        Public Sub PruneOldLogs(retentionDays As Integer)
            If retentionDays < 1 Then Return
            Try
                Dim cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays)
                Dim pattern = $"{_filenamePrefix}*.log"
                For Each filePath In IO.Directory.GetFiles(_directory, pattern)
                    Try
                        Dim filename = IO.Path.GetFileNameWithoutExtension(filePath)
                        Dim dateStr = filename.Substring(_filenamePrefix.Length)
                        Dim fileDate As DateTime
                        If Not DateTime.TryParseExact(dateStr, "yyyy-MM-dd",
                                                       Globalization.CultureInfo.InvariantCulture,
                                                       Globalization.DateTimeStyles.None,
                                                       fileDate) Then
                            Continue For
                        End If
                        If fileDate < cutoff Then
                            IO.File.Delete(filePath)
                        End If
                    Catch
                        ' Per-file failure non-fatal
                    End Try
                Next
            Catch
                ' Directory missing or unreadable — nothing to prune
            End Try
        End Sub
    End Class

    ''' <summary>
    ''' Per-category logger. All instances share the same underlying
    ''' writer via the provider — categories are just a label in each
    ''' written line.
    ''' </summary>
    Friend NotInheritable Class FileLogger
        Implements ILogger

        Private ReadOnly _provider As FileLoggerProvider
        Private ReadOnly _category As String

        Public Sub New(provider As FileLoggerProvider, category As String)
            _provider = provider
            _category = category
        End Sub

        Public Function BeginScope(Of TState)(state As TState) As IDisposable _
            Implements ILogger.BeginScope
            Return NullScope.Instance
        End Function

        Public Function IsEnabled(logLevel As LogLevel) As Boolean _
            Implements ILogger.IsEnabled
            Return _provider.IsLevelEnabled(logLevel)
        End Function

        Public Sub Log(Of TState)(logLevel As LogLevel, eventId As EventId,
                                    state As TState, exception As Exception,
                                    formatter As Func(Of TState, Exception, String)) _
            Implements ILogger.Log
            If Not IsEnabled(logLevel) Then Return
            If formatter Is Nothing Then Return
            Dim message = formatter.Invoke(state, exception)
            If String.IsNullOrEmpty(message) AndAlso exception Is Nothing Then Return
            _provider.WriteLine(logLevel, _category, message, exception)
        End Sub

        Private NotInheritable Class NullScope
            Implements IDisposable
            Public Shared ReadOnly Instance As New NullScope()
            Public Sub Dispose() Implements IDisposable.Dispose
            End Sub
        End Class
    End Class

End Namespace
