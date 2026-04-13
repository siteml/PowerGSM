Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging

' ============================================================
'  RingBufferStore
'
'  Maintains a fixed-capacity circular buffer of log lines
'  per instance. When the buffer is full, the oldest line
'  is dropped to make room for the new one.
'
'  Two-tier storage:
'    1. In-memory ConcurrentQueue for fast access and live
'       SSE streaming. This is the primary store.
'    2. SQLite (via NodeDatabase) for persistence across node
'       restarts and for the hot-reload checkpoint/replay path.
'       Writes to SQLite happen on a background thread to avoid
'       slowing down the stdout reader.
'
'  LineIndex is a monotonically increasing counter per instance.
'  It never resets, even when the buffer wraps. This is what
'  allows the hot-reload checkpoint/replay to work correctly:
'  the old parser records the last LineIndex it processed, and
'  the new parser asks for everything from that index onwards.
'
'  Thread safety:
'    InstanceBuffer uses ConcurrentQueue (lock-free reads/writes)
'    and Interlocked.Increment for the line counter.
'    The _buffers dictionary uses ConcurrentDictionary.
' ============================================================

Public Class RingBufferStore

    Private ReadOnly _db As NodeDatabase
    Private ReadOnly _config As NodeConfiguration
    Private ReadOnly _logger As ILogger(Of RingBufferStore)

    ' One buffer per instance. Created lazily on first write.
    Private ReadOnly _buffers As New ConcurrentDictionary(Of String, InstanceBuffer)(
        StringComparer.OrdinalIgnoreCase)

    ' Background writer queue - decouples the fast stdout reader
    ' from the slower SQLite write path.
    Private ReadOnly _writeQueue As New ConcurrentQueue(Of PendingWrite)()
    Private ReadOnly _writeTrigger As New SemaphoreSlim(0, Integer.MaxValue)
    Private ReadOnly _writerCts As New CancellationTokenSource()
    Private ReadOnly _writerTask As Task

    Public Sub New(db As NodeDatabase,
                   config As NodeConfiguration,
                   logger As ILogger(Of RingBufferStore))
        _db = db
        _config = config
        _logger = logger

        ' Start the background SQLite writer task.
        _writerTask = Task.Run(AddressOf BackgroundWriterAsync)
    End Sub


    ' ============================================================
    '  WRITE
    '  Called by the stdout reader on every log line.
    '  Must be fast - this is on the hot path.
    ' ============================================================

    Public Sub Append(instanceId As String,
                      sourceId As String,
                      timestamp As DateTime,
                      content As String)

        Dim buffer = _buffers.GetOrAdd(instanceId,
            Function(id) New InstanceBuffer(_config.RingBufferCapacity))

        ' Assign a monotonically increasing line index.
        Dim lineIndex = Interlocked.Increment(buffer.NextLineIndex) - 1

        Dim line As New BufferedLogLine With {
            .LineIndex = lineIndex,
            .InstanceId = instanceId,
            .SourceId = sourceId,
            .Timestamp = timestamp,
            .Content = content
        }

        ' Write to the in-memory buffer. If over capacity, drop
        ' the oldest entry (ConcurrentQueue: dequeue from front).
        buffer.Lines.Enqueue(line)
        Do While buffer.Lines.Count > buffer.Capacity
            Dim dropped As BufferedLogLine = Nothing
            buffer.Lines.TryDequeue(dropped)
        Loop

        ' Queue the SQLite write for the background writer.
        _writeQueue.Enqueue(New PendingWrite With {
            .InstanceId = instanceId,
            .LineIndex = lineIndex,
            .SourceId = sourceId,
            .Timestamp = timestamp,
            .Content = content
        })
        _writeTrigger.Release()   ' Signal the background writer.
    End Sub


    ' ============================================================
    '  READ
    ' ============================================================

    ' Returns the most recent N lines for an instance.
    ' Reads from in-memory buffer - fast, no I/O.
    Public Function GetRecent(instanceId As String,
                               count As Integer,
                               Optional sourceId As String = "") As IReadOnlyList(Of BufferedLogLine)

        Dim buffer As InstanceBuffer = Nothing
        If Not _buffers.TryGetValue(instanceId, buffer) Then
            Return Array.Empty(Of BufferedLogLine)()
        End If

        Dim all = buffer.Lines.ToArray()   ' Snapshot - thread safe

        If Not String.IsNullOrEmpty(sourceId) Then
            all = all.Where(Function(l) l.SourceId = sourceId).ToArray()
        End If

        ' Return the last N lines in chronological order.
        Return all.Skip(Math.Max(0, all.Length - count)).ToList().AsReadOnly()
    End Function

    ' Returns all lines from a given index onwards.
    ' Used for hot-reload checkpoint replay and SSE stream resume.
    ' Falls back to SQLite if the requested lines have been evicted
    ' from the in-memory buffer.
    Public Async Function GetFromIndexAsync(instanceId As String,
                                             fromIndex As Long,
                                             cancellation As CancellationToken) As Task(Of IReadOnlyList(Of BufferedLogLine))

        Dim buffer As InstanceBuffer = Nothing
        If _buffers.TryGetValue(instanceId, buffer) Then
            ' Check if the requested index is still in the in-memory buffer.
            Dim inMemory = buffer.Lines.ToArray()
            If inMemory.Length > 0 AndAlso inMemory(0).LineIndex <= fromIndex Then
                ' All requested lines are in memory.
                Return inMemory.Where(Function(l) l.LineIndex >= fromIndex).
                                OrderBy(Function(l) l.LineIndex).
                                ToList().AsReadOnly()
            End If
        End If

        ' Lines have been evicted - read from SQLite.
        _logger.LogDebug(
            "RingBuffer: falling back to SQLite for instance {Id} from index {Idx}",
            instanceId, fromIndex)

        Dim dbLines = _db.GetLinesFromIndex(instanceId, fromIndex)
        Return dbLines.Select(Function(r) New BufferedLogLine With {
            .LineIndex = r.LineIndex,
            .InstanceId = instanceId,
            .SourceId = r.SourceId,
            .Timestamp = r.Timestamp,
            .Content = r.Content
        }).ToList().AsReadOnly()
    End Function

    ' Returns the index of the last line written for an instance.
    ' Used by the hot-reload drain to record the checkpoint position.
    Public Function GetCurrentIndex(instanceId As String) As Long
        Dim buffer As InstanceBuffer = Nothing
        If Not _buffers.TryGetValue(instanceId, buffer) Then Return -1
        ' NextLineIndex is the NEXT index to assign, so subtract 1 for current.
        Return Interlocked.Read(buffer.NextLineIndex) - 1
    End Function

    ' Subscribe to new lines as they arrive - used by the SSE stream endpoint.
    ' The caller provides a callback that receives each new line.
    ' Returns a subscription token; dispose it to unsubscribe.
    Public Function Subscribe(instanceId As String,
                               callback As Action(Of BufferedLogLine)) As IDisposable
        Dim buffer = _buffers.GetOrAdd(instanceId,
            Function(id) New InstanceBuffer(_config.RingBufferCapacity))
        Dim subscription As New LineSubscription(callback)
        buffer.Subscribers.TryAdd(subscription.SubscriptionId, subscription)
        Return subscription
    End Function

    ' Called internally when a new line arrives - notifies all subscribers.
    Private Sub NotifySubscribers(instanceId As String, line As BufferedLogLine)
        Dim buffer As InstanceBuffer = Nothing
        If Not _buffers.TryGetValue(instanceId, buffer) Then Return
        For Each subscription In buffer.Subscribers.Values
            Try
                subscription.Callback.Invoke(line)
            Catch ex As Exception
                _logger.LogWarning(ex, "RingBuffer: subscriber error for {Id}", instanceId)
            End Try
        Next
    End Sub


    ' ============================================================
    '  BACKGROUND SQLITE WRITER
    '  Batches writes to SQLite to avoid hammering the DB on
    '  high-volume log output. Flushes every 50ms or 100 lines,
    '  whichever comes first.
    ' ============================================================

    Private Async Function BackgroundWriterAsync() As Task
        Const FlushIntervalMs As Integer = 50
        Const FlushBatchSize As Integer = 100

        Do
            Try
                ' Wait for at least one pending write, or flush interval.
                Await _writeTrigger.WaitAsync(FlushIntervalMs, _writerCts.Token)
            Catch ex As OperationCanceledException
                Exit Do
            End Try

            ' Drain the queue in batches.
            Dim batch As New List(Of PendingWrite)()
            Dim item As PendingWrite = Nothing
            Do While batch.Count < FlushBatchSize AndAlso _writeQueue.TryDequeue(item)
                batch.Add(item)
            Loop

            If batch.Count = 0 Then Continue Do

            Try
                For Each pendingWrite In batch
                    _db.AppendLogLine(
                        pendingWrite.InstanceId,
                        pendingWrite.LineIndex,
                        pendingWrite.SourceId,
                        pendingWrite.Timestamp,
                        pendingWrite.Content,
                        _config.RingBufferCapacity)
                Next
            Catch ex As Exception
                _logger.LogError(ex, "RingBuffer: SQLite write error")
            End Try
        Loop

        ' Flush any remaining writes on shutdown.
        Dim remaining As PendingWrite = Nothing
        Do While _writeQueue.TryDequeue(remaining)
            Try
                _db.AppendLogLine(remaining.InstanceId, remaining.LineIndex,
                                   remaining.SourceId, remaining.Timestamp,
                                   remaining.Content, _config.RingBufferCapacity)
            Catch
            End Try
        Loop
    End Function

    Public Sub Dispose()
        _writerCts.Cancel()
        _writerTask.Wait(TimeSpan.FromSeconds(5))
        _writerCts.Dispose()
    End Sub

End Class


' ============================================================
'  INSTANCE BUFFER
'  Per-instance in-memory state.
' ============================================================

Friend Class InstanceBuffer
    Public ReadOnly Property Capacity As Integer
    Public ReadOnly Lines As New ConcurrentQueue(Of BufferedLogLine)()
    ' Monotonically increasing. Use Interlocked.Increment to assign.
    Public NextLineIndex As Long = 0
    ' Live subscribers (SSE stream connections).
    Public ReadOnly Subscribers As New ConcurrentDictionary(Of String, LineSubscription)()

    Public Sub New(capacity As Integer)
        Me.Capacity = capacity
    End Sub
End Class


' ============================================================
'  LINE SUBSCRIPTION
'  Returned to SSE stream handlers. Disposing unregisters
'  the callback from the buffer's subscriber list.
' ============================================================

Friend Class LineSubscription
    Implements IDisposable

    Public ReadOnly Property SubscriptionId As String = Guid.NewGuid().ToString()
    Public ReadOnly Property Callback As Action(Of BufferedLogLine)
    Private _buffer As InstanceBuffer

    Public Sub New(callback As Action(Of BufferedLogLine))
        Me.Callback = callback
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dim removed As LineSubscription = Nothing
        If _buffer IsNot Nothing Then _buffer.Subscribers.TryRemove(SubscriptionId, removed)
    End Sub
End Class


' ============================================================
'  PENDING WRITE
'  Queued for the background SQLite writer.
' ============================================================

Friend Class PendingWrite
    Public Property InstanceId As String
    Public Property LineIndex As Long
    Public Property SourceId As String
    Public Property Timestamp As DateTime
    Public Property Content As String
End Class


' ============================================================
'  BUFFERED LOG LINE
'  The canonical line type used throughout the node.
' ============================================================

Public Class BufferedLogLine
    Public Property LineIndex As Long
    Public Property InstanceId As String
    Public Property SourceId As String
    Public Property Timestamp As DateTime
    Public Property Content As String
End Class
