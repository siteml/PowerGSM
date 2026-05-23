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
        '''
        ''' The tail capture and live-stream subscription must be
        ''' arranged atomically against the ring buffer's write
        ''' position — see SubscribeAndGetTail for why. Doing them
        ''' as two separate lock acquisitions (capture _writePos at
        ''' subscription, capture _writePos again at tail) produces
        ''' a double-emit window when Add() happens between the
        ''' two: lines added in that gap appear in both the tail
        ''' (because tail returns the newest N regardless of when
        ''' they were added) AND the live stream (because
        ''' LastSequence reflects the older _writePos and so
        ''' GetLinesSince returns them too). A fresh LO instance
        ''' start can produce dozens of duplicated lines this way
        ''' before the stream settles into its steady-state.
        ''' </summary>
        Public Async Function StreamToResponseAsync(instanceId As String,
                                                     response As HttpResponse,
                                                     tailLines As Integer,
                                                     cancellation As CancellationToken) As Task
            Dim buf = _buffers.GetOrAdd(instanceId,
                Function(id) New InstanceBuffer(id, DefaultBufferSize))

            ' Atomic: tail snapshot + subscription registration under
            ' the same lock acquisition. Anything added before this
            ' call returns is in the tail; anything added after this
            ' call returns will be in the live stream. No overlap.
            Dim subscription As New LineSubscription()
            Dim tail = buf.SubscribeAndGetTail(subscription, tailLines)

            Try
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

        ''' <summary>
        ''' Atomically captures the current tail AND registers a
        ''' new subscription, under a single lock acquisition.
        '''
        ''' Returns a snapshot of the last 'tailCount' lines in the
        ''' buffer at the moment the lock was held. Sets the
        ''' subscription's LastSequence such that GetLinesSince
        ''' (LastSequence) called by the caller will return only
        ''' lines added AFTER this method returned — no overlap
        ''' with the returned tail, no gap.
        '''
        ''' Required because the alternative of "AddSubscription
        ''' then GetTail" takes the lock twice; between the two
        ''' calls, Add() can fire and the line it inserts ends up
        ''' in BOTH the tail (which sees it because GetTail just
        ''' walks the last N entries from the current _writePos)
        ''' AND the live stream (because subscription.LastSequence
        ''' was captured from the older _writePos at
        ''' AddSubscription time, so GetLinesSince picks it up
        ''' again). The symmetric fix — update LastSequence to the
        ''' tail's last seq after sending tail — produces a gap
        ''' instead: lines added between the subscription request
        ''' and the tail capture but pushed out of the tail's
        ''' window are lost from both. Doing both under one lock
        ''' is the only seamless option.
        ''' </summary>
        Public Function SubscribeAndGetTail(subscription As LineSubscription,
                                             tailCount As Integer) As IReadOnlyList(Of BufferedLogLine)
            SyncLock _lock
                ' Inline GetTail under the lock, using a single
                ' captured value of _writePos for both the tail walk
                ' and the LastSequence assignment below. This is the
                ' invariant that makes the contract work: tail covers
                ' (_writePos - take) .. (_writePos - 1), live stream
                ' starts at _writePos.
                Dim available = CInt(Math.Min(_writePos, CLng(_ring.Length)))
                Dim take = Math.Min(tailCount, available)
                Dim result As New List(Of BufferedLogLine)(take)
                For i = _writePos - take To _writePos - 1
                    Dim idx = CInt(((i Mod _ring.Length) + _ring.Length) Mod _ring.Length)
                    If _ring(idx) IsNot Nothing Then
                        result.Add(_ring(idx))
                    End If
                Next

                subscription.LastSequence = _writePos - 1
                _subscriptions.Add(subscription)

                Return result
            End SyncLock
        End Function

        ''' <summary>
        ''' Legacy non-atomic subscription. Kept available for
        ''' subscribers that don't want a tail snapshot (no current
        ''' callers; SubscribeAndGetTail is the right choice when
        ''' streaming SSE because of the overlap window described
        ''' there). If a future caller wants subscribe-only without
        ''' backfill, this still works correctly — there's nothing
        ''' for the tail to overlap with.
        ''' </summary>
        Public Sub AddSubscription(subscription As LineSubscription)
            SyncLock _lock
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
