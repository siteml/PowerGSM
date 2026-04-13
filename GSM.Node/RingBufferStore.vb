Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.AspNetCore.Http

' ============================================================
'  RingBufferStore — log line storage with SSE streaming
'
'  Each instance gets its own InstanceBuffer (fixed-size ring).
'  The manager connects via SSE and receives a tail of recent
'  lines followed by a live stream of new lines.
'
'  Subscribers are notified via SemaphoreSlim so they can wake
'  and read new lines without polling.
' ============================================================

Namespace GSM.Node

    ''' <summary>
    ''' A single buffered log line with metadata.
    ''' </summary>
    Public Class BufferedLogLine
        Public Property Timestamp As DateTime
        Public Property Text As String
        Public Property IsError As Boolean
        Public Property SequenceNumber As Long
    End Class

    ''' <summary>
    ''' Central store managing per-instance ring buffers.
    ''' </summary>
    Public Class RingBufferStore

        Private ReadOnly _buffers As New ConcurrentDictionary(Of String, InstanceBuffer)

        Private Const DefaultBufferSize As Integer = 4096

        ''' <summary>
        ''' Appends a log line to the instance's ring buffer and
        ''' notifies any active subscribers (SSE streams).
        ''' </summary>
        Public Sub Append(instanceId As String, line As BufferedLogLine)
            Dim buf = _buffers.GetOrAdd(instanceId,
                Function(id) New InstanceBuffer(id, DefaultBufferSize))
            buf.Add(line)
        End Sub

        ''' <summary>
        ''' Returns the most recent lines for an instance.
        ''' </summary>
        Public Function GetTail(instanceId As String,
                                count As Integer) As IReadOnlyList(Of BufferedLogLine)
            Dim buf As InstanceBuffer = Nothing
            If Not _buffers.TryGetValue(instanceId, buf) Then
                Return Array.Empty(Of BufferedLogLine)()
            End If
            Return buf.GetTail(count)
        End Function

        ''' <summary>
        ''' Streams log lines as SSE events to the HTTP response.
        ''' Sends tail lines first, then follows new lines until
        ''' the cancellation token fires.
        ''' </summary>
        Public Async Function StreamToResponseAsync(instanceId As String,
                                                     response As HttpResponse,
                                                     tailLines As Integer,
                                                     cancellation As CancellationToken) As Task
            Dim buf = _buffers.GetOrAdd(instanceId,
                Function(id) New InstanceBuffer(id, DefaultBufferSize))

            ' Create a subscription for new line notifications
            Dim subscription As New LineSubscription()
            buf.AddSubscription(subscription)

            Try
                ' Send tail first
                Dim tail = buf.GetTail(tailLines)
                For Each line In tail
                    Await WriteSseLineAsync(response, line, cancellation)
                Next
                Await response.Body.FlushAsync(cancellation)

                ' Follow new lines
                While Not cancellation.IsCancellationRequested
                    ' Wait for notification of new lines
                    Await subscription.WaitAsync(cancellation)

                    ' Drain all new lines since our last read position
                    Dim newLines = buf.GetLinesSince(subscription.LastSequence)
                    For Each line In newLines
                        Await WriteSseLineAsync(response, line, cancellation)
                        subscription.LastSequence = line.SequenceNumber
                    Next
                    Await response.Body.FlushAsync(cancellation)
                End While
            Catch ex As OperationCanceledException
                ' Normal — client disconnected
            Finally
                buf.RemoveSubscription(subscription)
            End Try
        End Function

        ''' <summary>
        ''' Removes the buffer for an instance (e.g. when instance is removed).
        ''' </summary>
        Public Sub RemoveBuffer(instanceId As String)
            Dim removed As InstanceBuffer = Nothing
            _buffers.TryRemove(instanceId, removed)
        End Sub

        Private Shared Async Function WriteSseLineAsync(response As HttpResponse,
                                                         line As BufferedLogLine,
                                                         cancellation As CancellationToken) As Task
            Dim json = JsonSerializer.Serialize(New With {
                .timestamp = line.Timestamp.ToString("o"),
                .text = line.Text,
                .isError = line.IsError,
                .seq = line.SequenceNumber
            })
            Dim sseData = $"data: {json}" & vbLf & vbLf
            Await response.WriteAsync(sseData, cancellation)
        End Function

    End Class

    ' ============================================================
    '  InstanceBuffer — fixed-size ring buffer per instance
    ' ============================================================

    Friend Class InstanceBuffer

        Private ReadOnly _instanceId As String
        Private ReadOnly _ring() As BufferedLogLine
        Private _writePos As Long = 0
        Private ReadOnly _lock As New Object()
        Private ReadOnly _subscriptions As New List(Of LineSubscription)

        Public Sub New(instanceId As String, capacity As Integer)
            _instanceId = instanceId
            ReDim _ring(capacity - 1)
        End Sub

        ''' <summary>
        ''' Adds a line to the ring buffer, assigns a sequence number,
        ''' and notifies all subscribers.
        ''' </summary>
        Public Sub Add(line As BufferedLogLine)
            SyncLock _lock
                line.SequenceNumber = _writePos
                _ring(CInt(_writePos Mod _ring.Length)) = line
                _writePos += 1

                ' Notify subscribers
                For Each subItem In _subscriptions
                    subItem.Signal()
                Next
            End SyncLock
        End Sub

        ''' <summary>
        ''' Returns the most recent N lines.
        ''' </summary>
        Public Function GetTail(count As Integer) As IReadOnlyList(Of BufferedLogLine)
            SyncLock _lock
                Dim available = CInt(Math.Min(_writePos, CLng(_ring.Length)))
                Dim take = Math.Min(count, available)
                Dim result As New List(Of BufferedLogLine)(take)
                For i = _writePos - take To _writePos - 1
                    Dim idx = CInt(((i Mod _ring.Length) + _ring.Length) Mod _ring.Length)
                    If _ring(idx) IsNot Nothing Then
                        result.Add(_ring(idx))
                    End If
                Next
                Return result
            End SyncLock
        End Function

        ''' <summary>
        ''' Returns lines with sequence numbers greater than the given value.
        ''' </summary>
        Public Function GetLinesSince(lastSequence As Long) As IReadOnlyList(Of BufferedLogLine)
            SyncLock _lock
                Dim result As New List(Of BufferedLogLine)
                Dim startSeq = lastSequence + 1
                ' Clamp to what's still in the buffer
                Dim oldestInBuffer = Math.Max(0, _writePos - _ring.Length)
                If startSeq < oldestInBuffer Then startSeq = oldestInBuffer

                For seq = startSeq To _writePos - 1
                    Dim idx = CInt(((seq Mod _ring.Length) + _ring.Length) Mod _ring.Length)
                    If _ring(idx) IsNot Nothing AndAlso _ring(idx).SequenceNumber = seq Then
                        result.Add(_ring(idx))
                    End If
                Next
                Return result
            End SyncLock
        End Function

        Public Sub AddSubscription(subscription As LineSubscription)
            SyncLock _lock
                ' Set initial read position to current write head
                subscription.LastSequence = _writePos - 1
                _subscriptions.Add(subscription)
            End SyncLock
        End Sub

        Public Sub RemoveSubscription(subscription As LineSubscription)
            SyncLock _lock
                _subscriptions.Remove(subscription)
            End SyncLock
        End Sub

    End Class

    ' ============================================================
    '  LineSubscription — notification mechanism for SSE followers
    ' ============================================================

    Friend Class LineSubscription

        Private ReadOnly _semaphore As New SemaphoreSlim(0)
        Public Property LastSequence As Long

        ''' <summary>
        ''' Signals that new lines are available.
        ''' </summary>
        Public Sub Signal()
            ' Release only if someone is waiting
            If _semaphore.CurrentCount = 0 Then
                Try
                    _semaphore.Release()
                Catch ex As SemaphoreFullException
                    ' Already signalled
                End Try
            End If
        End Sub

        ''' <summary>
        ''' Waits until new lines are signalled or cancellation.
        ''' </summary>
        Public Async Function WaitAsync(cancellation As CancellationToken) As Task
            Await _semaphore.WaitAsync(cancellation)
        End Function

    End Class

End Namespace
