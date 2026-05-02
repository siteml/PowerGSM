Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging

' ============================================================
'  GSM.Node.Security — abuse prevention services
'
'  Three layers of defense for an internet-exposed node:
'    1. Per-IP request rate limit (RequestRateTracker)
'    2. Per-IP failed-auth lockout (AuthFailureTracker)
'    3. Constant-time token comparison (SecurityHelpers)
' ============================================================

Namespace GSM.Node.Security

    ''' <summary>
    ''' Bound from the "Security" section of nodesettings.json.
    ''' Defaults are tuned for a single Manager talking to the node;
    ''' raise RequestsPerMinutePerIp if you have many concurrent
    ''' instances under heavy polling.
    ''' </summary>
    Public Class SecurityConfiguration

        ' How many failed auth attempts an IP gets within
        ' FailureWindowMinutes before being locked out.
        Public Property MaxFailedAttempts As Integer = 10

        ' Sliding window over which failures are counted.
        Public Property FailureWindowMinutes As Integer = 5

        ' How long an IP is locked out after exceeding the threshold.
        Public Property LockoutMinutes As Integer = 15

        ' Sleep duration on failed auth. Slows brute-force loops
        ' without measurably affecting honest reconnection.
        Public Property AuthFailureDelayMs As Integer = 250

        ' Per-IP global request rate (req/min). Catches flooding
        ' that doesn't go through the auth path (e.g. /api/version
        ' probing). Set to 0 to disable.
        Public Property RequestsPerMinutePerIp As Integer = 600

        ' Max request body in bytes. Kestrel default is 30MB; the
        ' node only ever receives small JSON payloads.
        Public Property MaxRequestBodyBytes As Long = 4194304L  ' 4MB

        ' Max concurrent open connections per Kestrel.
        Public Property MaxConcurrentConnections As Integer = 100

    End Class

    ''' <summary>
    ''' Constant-time comparison helper. Hashes both inputs to a
    ''' fixed 32-byte digest so FixedTimeEquals can be used regardless
    ''' of input length (and length itself is not leaked).
    ''' </summary>
    Public Module SecurityHelpers

        Public Function FixedTimeStringEquals(a As String, b As String) As Boolean
            If a Is Nothing Then a = String.Empty
            If b Is Nothing Then b = String.Empty
            Dim aBytes = Encoding.UTF8.GetBytes(a)
            Dim bBytes = Encoding.UTF8.GetBytes(b)
            Dim aHash = SHA256.HashData(aBytes)
            Dim bHash = SHA256.HashData(bBytes)
            Return CryptographicOperations.FixedTimeEquals(aHash, bHash)
        End Function

    End Module

    ' ============================================================
    '  AuthFailureTracker — per-IP failed-auth lockout
    ' ============================================================

    ''' <summary>
    ''' Tracks failed auth attempts per IP address. After
    ''' MaxFailedAttempts within FailureWindowMinutes, the IP is
    ''' locked out for LockoutMinutes. State is in-memory only;
    ''' a node restart clears all tracking.
    ''' </summary>
    Public Class AuthFailureTracker
        Implements IDisposable

        Private Class IpEntry
            Public Failures As New List(Of DateTime)
            Public LockedUntilUtc As DateTime = DateTime.MinValue
        End Class

        Private ReadOnly _entries As New ConcurrentDictionary(Of String, IpEntry)
        Private ReadOnly _config As SecurityConfiguration
        Private ReadOnly _logger As ILogger(Of AuthFailureTracker)
        Private ReadOnly _cleanupTimer As Timer
        Private _disposed As Boolean

        Public Sub New(config As SecurityConfiguration,
                       logger As ILogger(Of AuthFailureTracker))
            _config = config
            _logger = logger
            _cleanupTimer = New Timer(AddressOf CleanupCallback, Nothing,
                                      TimeSpan.FromMinutes(5),
                                      TimeSpan.FromMinutes(5))
        End Sub

        ''' <summary>
        ''' Returns the UTC time at which a lockout expires, or
        ''' DateTime.MinValue if the IP is not currently locked out.
        ''' </summary>
        Public Function GetLockoutExpiry(ip As String) As DateTime
            If String.IsNullOrEmpty(ip) Then Return DateTime.MinValue
            Dim entry As IpEntry = Nothing
            If _entries.TryGetValue(ip, entry) Then
                SyncLock entry
                    If entry.LockedUntilUtc > DateTime.UtcNow Then
                        Return entry.LockedUntilUtc
                    End If
                End SyncLock
            End If
            Return DateTime.MinValue
        End Function

        ''' <summary>
        ''' Records a failed auth attempt. Returns True if this
        ''' attempt pushed the IP over the threshold and triggered
        ''' a fresh lockout.
        ''' </summary>
        Public Function RecordFailure(ip As String) As Boolean
            If String.IsNullOrEmpty(ip) Then Return False
            Dim entry = _entries.GetOrAdd(ip, Function(k) New IpEntry())
            SyncLock entry
                Dim now = DateTime.UtcNow
                Dim windowStart = now.AddMinutes(-_config.FailureWindowMinutes)
                entry.Failures.RemoveAll(Function(t) t < windowStart)
                entry.Failures.Add(now)
                If entry.Failures.Count >= _config.MaxFailedAttempts Then
                    entry.LockedUntilUtc = now.AddMinutes(_config.LockoutMinutes)
                    _logger.LogWarning(
                        "IP {Ip} locked out until {Until:o} after {Count} failed auth attempts",
                        ip, entry.LockedUntilUtc, entry.Failures.Count)
                    Return True
                End If
            End SyncLock
            Return False
        End Function

        ''' <summary>
        ''' Clears failure history for an IP after a successful auth
        ''' so legitimate clients don't accumulate noise toward lockout.
        ''' Does NOT clear an active lockout — only the failure list.
        ''' </summary>
        Public Sub Reset(ip As String)
            If String.IsNullOrEmpty(ip) Then Return
            Dim entry As IpEntry = Nothing
            If _entries.TryGetValue(ip, entry) Then
                SyncLock entry
                    entry.Failures.Clear()
                End SyncLock
            End If
        End Sub

        Private Sub CleanupCallback(state As Object)
            Try
                Dim now = DateTime.UtcNow
                Dim windowStart = now.AddMinutes(-_config.FailureWindowMinutes)
                For Each kvp In _entries.ToArray()
                    Dim entry = kvp.Value
                    SyncLock entry
                        entry.Failures.RemoveAll(Function(t) t < windowStart)
                        If entry.Failures.Count = 0 AndAlso
                           entry.LockedUntilUtc <= now Then
                            Dim removed As IpEntry = Nothing
                            _entries.TryRemove(kvp.Key, removed)
                        End If
                    End SyncLock
                Next
            Catch ex As Exception
                _logger.LogWarning(ex, "Auth failure tracker cleanup failed")
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _cleanupTimer?.Dispose()
        End Sub

    End Class

    ' ============================================================
    '  RequestRateTracker — per-IP global request throttle
    ' ============================================================

    ''' <summary>
    ''' Per-IP sliding-window request rate limiter. Independent of
    ''' auth state — applies to every request including unauthenticated
    ''' /api/version. Catches flooding that wouldn't trip the
    ''' AuthFailureTracker.
    ''' </summary>
    Public Class RequestRateTracker
        Implements IDisposable

        Private Class IpRequestEntry
            Public Timestamps As New List(Of DateTime)
        End Class

        Private ReadOnly _entries As New ConcurrentDictionary(Of String, IpRequestEntry)
        Private ReadOnly _config As SecurityConfiguration
        Private ReadOnly _cleanupTimer As Timer
        Private _disposed As Boolean

        Public Sub New(config As SecurityConfiguration)
            _config = config
            _cleanupTimer = New Timer(AddressOf CleanupCallback, Nothing,
                                      TimeSpan.FromMinutes(5),
                                      TimeSpan.FromMinutes(5))
        End Sub

        ''' <summary>
        ''' Records a request and returns True if the IP has now
        ''' exceeded the configured per-minute rate. When True is
        ''' returned the request is NOT counted against the bucket
        ''' (so rejection itself doesn't extend the lockout window).
        ''' </summary>
        Public Function IsOverLimit(ip As String) As Boolean
            If String.IsNullOrEmpty(ip) Then Return False
            If _config.RequestsPerMinutePerIp <= 0 Then Return False

            Dim entry = _entries.GetOrAdd(ip, Function(k) New IpRequestEntry())
            SyncLock entry
                Dim now = DateTime.UtcNow
                Dim windowStart = now.AddMinutes(-1)
                entry.Timestamps.RemoveAll(Function(t) t < windowStart)
                If entry.Timestamps.Count >= _config.RequestsPerMinutePerIp Then
                    Return True
                End If
                entry.Timestamps.Add(now)
                Return False
            End SyncLock
        End Function

        Private Sub CleanupCallback(state As Object)
            Try
                Dim now = DateTime.UtcNow
                Dim windowStart = now.AddMinutes(-1)
                For Each kvp In _entries.ToArray()
                    Dim entry = kvp.Value
                    SyncLock entry
                        entry.Timestamps.RemoveAll(Function(t) t < windowStart)
                        If entry.Timestamps.Count = 0 Then
                            Dim removed As IpRequestEntry = Nothing
                            _entries.TryRemove(kvp.Key, removed)
                        End If
                    End SyncLock
                Next
            Catch
                ' Cleanup is best-effort; swallow exceptions.
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _cleanupTimer?.Dispose()
        End Sub

    End Class

End Namespace
