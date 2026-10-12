Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Hosting.Systemd
Imports Microsoft.Extensions.Logging

' ============================================================
'  Phase 8-2 hardening follow-up 1 — self-test-gated readiness
'
'  Under systemd the unit is Type=notify (slice 8b-2), which makes
'  READY=1 the signal the whole update health-gate keys off: a start
'  that never sends READY is a FAILED start, and the ExecStartPre
'  marker/revert machinery rolls the binary back. Before this
'  follow-up, READY was sent automatically by SystemdLifetime the
'  moment the host started — "started" stood in for "correct", which
'  is exactly the weak gate the July 2026 incident exposed (node A:
'  an ancient binary RAN, so no revert fired).
'
'  This file closes part of that gap:
'
'  - GatedSystemdNotifier wraps the real SystemdNotifier and HOLDS
'    the READY notification until the startup self-check passes.
'    SystemdLifetime resolves ISystemdNotifier from DI, and
'    NodeProgram registers this gate AFTER UseSystemd(), so the
'    lifetime's automatic READY goes through the gate. Every other
'    notification (STOPPING, ...) passes straight through, so
'    shutdown behaviour is untouched.
'
'  - StartupSelfTest runs the actual checks once the host reports
'    ApplicationStarted (Kestrel is listening by then): config
'    sanity, database opens and answers a query, a shim binary is
'    resolvable, and the node's own /api/version answers over
'    loopback. Pass -> open the gate (READY goes out) and start the
'    8b-2 marker-clear grace timer. Fail -> NodeProgram stops the
'    host gracefully (shims detach, games keep running) and exits
'    non-zero, so systemd sees a failed start and the 8b-2 revert
'    path (Restart=on-failure -> ExecStartPre marker check) takes
'    over.
'
'  Scope notes:
'  - systemd-only by design. Windows / bare nodes already have an
'    external health gate (the NodeSetup survivor polls /api/version
'    and reverts); a node whose endpoint is dead fails that poll with
'    no help from this code.
'  - This catches GROSS breakage (can't open DB, endpoint dead, shim
'    tree missing). It deliberately does not try to catch a binary
'    that limps convincingly on stale dependencies — follow-ups 2/3
'    covered that class and were dropped as YAGNI (self-contained
'    releases made the enabling condition unreproducible).
'  - Budget: the endpoint probe retries up to ~30 s; everything else
'    is near-instant. Well inside systemd's default TimeoutStartSec
'    (90 s) — the unit doesn't set its own.
' ============================================================

Namespace GSM.Node

    ''' <summary>
    ''' ISystemdNotifier that forwards everything to the real
    ''' SystemdNotifier EXCEPT ready-state notifications, which are held
    ''' until <see cref="OpenGate"/> is called (startup self-check passed).
    ''' Ready is sent at most once; duplicate READY datagrams would be
    ''' harmless to systemd, but there's no reason to emit them.
    ''' </summary>
    Public NotInheritable Class GatedSystemdNotifier
        Implements ISystemdNotifier

        Private ReadOnly _inner As New SystemdNotifier()
        Private ReadOnly _lock As New Object()
        Private _gateOpen As Boolean
        Private _readySent As Boolean

        ''' <summary>
        ''' Pass-through of the real notifier's enablement (True when
        ''' a NOTIFY_SOCKET is present, i.e. actually running under
        ''' systemd with Type=notify). The gate only withholds READY —
        ''' it must not misreport whether systemd notification exists
        ''' at all, or SystemdLifetime would skip notifications
        ''' entirely and the whole readiness gate would be moot.
        ''' </summary>
        Public ReadOnly Property IsEnabled As Boolean Implements ISystemdNotifier.IsEnabled
            Get
                Return _inner.IsEnabled
            End Get
        End Property

        Public Sub Notify(state As ServiceState) Implements ISystemdNotifier.Notify
            ' ServiceState is an opaque struct; the stable comparison is its
            ' wire form ("READY=1").
            If state.ToString() = ServiceState.Ready.ToString() Then
                SyncLock _lock
                    If Not _gateOpen OrElse _readySent Then Return
                    _readySent = True
                End SyncLock
                _inner.Notify(ServiceState.Ready)
                Return
            End If
            _inner.Notify(state)
        End Sub

        ''' <summary>
        ''' Self-check passed: release READY. Sends it directly rather than
        ''' waiting for another Notify — by the time the check runs, the
        ''' host HAS started (the check is triggered off ApplicationStarted),
        ''' so SystemdLifetime's own READY attempt has already been and gone
        ''' through the closed gate.
        ''' </summary>
        Public Sub OpenGate()
            SyncLock _lock
                If _gateOpen Then Return
                _gateOpen = True
                If _readySent Then Return
                _readySent = True
            End SyncLock
            _inner.Notify(ServiceState.Ready)
        End Sub

    End Class

    ''' <summary>
    ''' The startup self-check itself. Pure checks — policy (gate open /
    ''' host stop / exit code) lives in NodeProgram's driver.
    ''' </summary>
    Friend Module StartupSelfTest

        ''' <summary>
        ''' Process exit code for "startup self-check failed" (node exit
        ''' codes so far: 10 = update-exit for systemd relaunch).
        ''' Non-zero so Restart=on-failure treats it as the failed start
        ''' it is.
        ''' </summary>
        Friend Const ExitCodeSelfTestFailed As Integer = 12

        Private Const EndpointProbeBudgetSeconds As Integer = 30
        Private Const EndpointProbePerTrySeconds As Integer = 5

        ''' <summary>
        ''' Run all checks. Returns True when every check passed. Each
        ''' failure is logged at Critical with enough detail to act on;
        ''' a pass logs one Information summary line. Never throws.
        ''' </summary>
        Friend Async Function RunAsync(config As NodeConfiguration,
                                       db As NodeDatabase,
                                       logger As ILogger) As Task(Of Boolean)
            Dim sw = Stopwatch.StartNew()

            ' 1. Config sanity — the config bound and carries usable values.
            If config Is Nothing Then
                logger.LogCritical("Startup self-test FAILED: NodeConfiguration did not resolve.")
                Return False
            End If
            If config.ListenPort < 1 OrElse config.ListenPort > 65535 Then
                logger.LogCritical(
                    "Startup self-test FAILED: ListenPort {Port} is out of range.", config.ListenPort)
                Return False
            End If
            If String.IsNullOrEmpty(config.DataDirectory) OrElse Not Directory.Exists(config.DataDirectory) Then
                logger.LogCritical(
                    "Startup self-test FAILED: DataDirectory '{Dir}' is missing.", config.DataDirectory)
                Return False
            End If

            ' 2. Database — actually opens and answers a trivial query.
            Try
                db.VerifyReadable()
            Catch ex As Exception
                logger.LogCritical(ex, "Startup self-test FAILED: node.db did not open/answer.")
                Return False
            End Try

            ' 3. Shim reachable — the same resolution ShimSession uses at
            '    spawn time finds an existing shim binary.
            Dim shimVersion As String = ""
            Dim shimPath As String = ""
            Try
                shimPath = ShimSession.ResolveShimExePath(shimVersion)
            Catch
                shimPath = ""
            End Try
            If String.IsNullOrEmpty(shimPath) OrElse Not File.Exists(shimPath) Then
                logger.LogCritical(
                    "Startup self-test FAILED: no shim binary resolvable under {Root} (instance starts would fail).",
                    Path.Combine(AppContext.BaseDirectory, "GSM.Shim"))
                Return False
            End If

            ' 4. Endpoint self-probe — the node answers its own /api/version
            '    over loopback, on whichever listener the config says exists
            '    (plain HTTP unless HttpMode is Disabled, then HTTPS with the
            '    self-signed cert accepted — reachability is the question
            '    here, not trust).
            Dim probeError = Await ProbeEndpointAsync(config).ConfigureAwait(False)
            If probeError IsNot Nothing Then
                logger.LogCritical("Startup self-test FAILED: {Error}", probeError)
                Return False
            End If

            logger.LogInformation(
                "Startup self-test passed in {Ms} ms (config, db, shim {ShimVer}, endpoint) — READY released.",
                sw.ElapsedMilliseconds, If(String.IsNullOrEmpty(shimVersion), "(flat)", shimVersion))
            Return True
        End Function

        ''' <summary>
        ''' Poll the node's own version endpoint over loopback until it
        ''' answers 2xx or the budget elapses. Returns Nothing on success,
        ''' else a description of what was probed and the last error.
        ''' </summary>
        Private Async Function ProbeEndpointAsync(config As NodeConfiguration) As Task(Of String)
            Dim useHttps = config.HttpMode.Trim().Equals("Disabled", StringComparison.OrdinalIgnoreCase)
            Dim url As String
            If useHttps Then
                url = $"https://127.0.0.1:{config.EffectiveTlsPort}/api/version"
            Else
                url = $"http://127.0.0.1:{config.ListenPort}/api/version"
            End If

            Dim handler As New HttpClientHandler()
            If useHttps Then
                ' Reachability probe against our own self-signed cert.
                handler.ServerCertificateCustomValidationCallback =
                    Function(msg, cert, chain, errors) True
            End If

            Using client As New HttpClient(handler)
                client.Timeout = TimeSpan.FromSeconds(EndpointProbePerTrySeconds)
                Dim deadline = DateTime.UtcNow.AddSeconds(EndpointProbeBudgetSeconds)
                Dim lastError As String = "no attempt completed"
                Do
                    Try
                        Using resp = Await client.GetAsync(url).ConfigureAwait(False)
                            If resp.IsSuccessStatusCode Then Return Nothing
                            lastError = $"HTTP {CInt(resp.StatusCode)}"
                        End Using
                    Catch ex As Exception
                        lastError = ex.GetBaseException().Message
                    End Try
                    If DateTime.UtcNow >= deadline Then Exit Do
                    Await Task.Delay(1000).ConfigureAwait(False)
                Loop
                Return $"{url} not answering after {EndpointProbeBudgetSeconds}s ({lastError})"
            End Using
        End Function

    End Module

End Namespace
