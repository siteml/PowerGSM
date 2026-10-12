Imports System
Imports System.IO
Imports System.Runtime.Versioning
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions

' ============================================================
'  ConfigHelpers — pure functions used by both UIs
'
'  Centralizes token generation and field validation so the GUI
'  and CLI surface the same rules and the same error messages.
'  Validation functions return Nothing on success or a human-
'  readable error string on failure (also used as warnings —
'  see ValidatePort).
' ============================================================

Public Module ConfigHelpers

    ' 36 random bytes -> 48 base64 characters. Comfortable margin over
    ' 32 bytes (256 bits) of entropy and matches the openssl rand -base64
    ' suggestion in the project reference doc. The "+/=" alphabet is fine
    ' for an Authorization header and survives JSON round-trip.
    Private Const TokenByteLength As Integer = 36

    Public Function GenerateAuthToken() As String
        Dim bytes(TokenByteLength - 1) As Byte
        Using rng = RandomNumberGenerator.Create()
            rng.GetBytes(bytes)
        End Using
        Return Convert.ToBase64String(bytes)
    End Function

    Public Function IsAuthTokenPlaceholder(token As String) As Boolean
        If String.IsNullOrWhiteSpace(token) Then Return True
        ' Match exactly the literal the node ships with and any obvious
        ' user-edited variants. Case-insensitive to catch lower-case typing.
        Return token.Trim().Equals(NodeSetupConfig.PlaceholderToken,
                                   StringComparison.OrdinalIgnoreCase)
    End Function

    ''' <summary>
    ''' Returns Nothing when valid; an error message when invalid;
    ''' a "Warning: ..." string when the value is technically valid
    ''' but likely problematic (e.g. ports below 1024). Callers that
    ''' want to be strict can check the prefix.
    ''' </summary>
    Public Function ValidatePort(portValue As Integer) As String
        If portValue < 1 OrElse portValue > 65535 Then
            Return "Port must be between 1 and 65535."
        End If
        If portValue < 1024 Then
            Return "Warning: ports below 1024 typically require root/administrator privileges."
        End If
        Return Nothing
    End Function

    Public Function ValidateNodeId(nodeId As String) As String
        If String.IsNullOrWhiteSpace(nodeId) Then
            Return "Node ID cannot be empty."
        End If
        If nodeId.Length > 128 Then
            Return "Node ID is unusually long; consider keeping it under 128 characters."
        End If
        ' We intentionally do not constrain the character set further. The
        ' Manager treats it as opaque text. Spaces are fine.
        Return Nothing
    End Function

    Public Function ValidateDataDirectory(dataDir As String) As String
        Return ValidateDirectoryPath(dataDir, "Data directory")
    End Function

    ''' <summary>
    ''' Same shape as ValidateDataDirectory but with messages keyed
    ''' to the ServersDirectory field, so users see "Servers directory"
    ''' rather than "Data directory" when they're editing the wrong
    ''' value. Both use the shared ValidateDirectoryPath helper so
    ''' adding a constraint (e.g. forbidding the data directory and
    ''' servers directory from being equal) only needs one edit.
    ''' </summary>
    Public Function ValidateServersDirectory(serversDir As String) As String
        Return ValidateDirectoryPath(serversDir, "Servers directory")
    End Function

    ''' <summary>
    ''' Shared directory-path validation used by both ValidateDataDirectory
    ''' and ValidateServersDirectory. Rejects empty values and obviously
    ''' invalid path characters; returns a "Note: ..." warning (which the
    ''' caller treats as accept-with-message) for relative paths because
    ''' relative resolution depends on the working directory at node
    ''' startup, which the operator may not control precisely — services
    ''' in particular often run with a working directory the operator
    ''' didn't choose.
    ''' </summary>
    Private Function ValidateDirectoryPath(dirValue As String, fieldLabel As String) As String
        If String.IsNullOrWhiteSpace(dirValue) Then
            Return fieldLabel & " cannot be empty."
        End If

        ' Reject obvious garbage but allow both relative ("./data") and
        ' absolute paths. Relative paths resolve relative to the node's
        ' working directory at runtime, which the user may not control
        ' precisely (e.g. when running as a service), so warn about that.
        Try
            Dim invalidChars = Path.GetInvalidPathChars()
            For Each c In invalidChars
                If dirValue.IndexOf(c) >= 0 Then
                    Return fieldLabel & " contains an invalid character."
                End If
            Next
        Catch
            Return fieldLabel & " is not a valid path."
        End Try

        If Not Path.IsPathRooted(dirValue) Then
            Return "Note: relative paths resolve against the node's working directory at runtime. " &
                   "When running as a service the working directory may not be the install folder."
        End If
        Return Nothing
    End Function

    Public Function ValidateConcurrentInstalls(maxConcurrent As Integer) As String
        If maxConcurrent < 1 Then Return "Must allow at least 1 concurrent install."
        If maxConcurrent > 16 Then Return "Warning: more than 16 concurrent installs is rarely useful and may saturate the network."
        Return Nothing
    End Function

    Public Function ValidateLogRetentionDays(days As Integer) As String
        If days < 1 Then Return "Log retention must be at least 1 day."
        If days > 3650 Then Return "Log retention is capped at 10 years (3650 days)."
        Return Nothing
    End Function

    Public Function ValidateMetricsInterval(seconds As Integer) As String
        If seconds < 1 Then Return "Metrics interval must be at least 1 second."
        If seconds > 3600 Then Return "Metrics interval should be at most 1 hour."
        Return Nothing
    End Function

    ' ---- Transport (HTTPS_SinglePort_Plan slice 2) ----

    Public ReadOnly HttpModes As String() = {"Disabled", "LoopbackOnly", "Open"}

    ''' <summary>
    ''' Canonical casing for an HttpMode string ("disabled" → "Disabled");
    ''' Nothing when it isn't one of the three modes.
    ''' </summary>
    Public Function NormalizeHttpMode(mode As String) As String
        If String.IsNullOrWhiteSpace(mode) Then Return Nothing
        For Each m In HttpModes
            If String.Equals(m, mode.Trim(), StringComparison.OrdinalIgnoreCase) Then Return m
        Next
        Select Case mode.Trim().ToLowerInvariant()
            Case "none", "off", "no" : Return "Disabled"
            Case "localhost", "loopback", "local" : Return "LoopbackOnly"
            Case "all", "yes", "on" : Return "Open"
        End Select
        Return Nothing
    End Function

    Public Function ValidateHttpMode(mode As String) As String
        If NormalizeHttpMode(mode) Is Nothing Then
            Return "Plain HTTP must be one of: Disabled, LoopbackOnly, Open."
        End If
        Return Nothing
    End Function

    ''' <summary>
    ''' TLS port: 0 = share the listen port (single-port, the default);
    ''' otherwise a valid port. Equal to the listen port is accepted and
    ''' normalised to 0 by the caller.
    ''' </summary>
    Public Function ValidateTlsPort(tlsPort As Integer, listenPort As Integer) As String
        If tlsPort = 0 Then Return Nothing
        If tlsPort < 1 OrElse tlsPort > 65535 Then
            Return "HTTPS port must be 0 (same as the listen port) or between 1 and 65535."
        End If
        If tlsPort = listenPort Then Return Nothing
        If tlsPort < 1024 Then
            Return "Warning: ports below 1024 typically require root/administrator privileges."
        End If
        Return Nothing
    End Function

    ''' <summary>
    ''' Applies the transport invariants before save: TlsPort equal to
    ''' ListenPort collapses to 0 (shared port); HttpMode is canonical-
    ''' cased; without HTTPS, HttpMode falls back to Open (the node would
    ''' coerce it anyway, and "Disabled" with no HTTPS = unreachable).
    ''' </summary>
    Public Sub NormalizeTransport(node As NodeSection)
        If node Is Nothing Then Return
        If node.TlsPort = node.ListenPort Then node.TlsPort = 0
        If node.TlsPort < 0 Then node.TlsPort = 0
        Dim mode = NormalizeHttpMode(node.HttpMode)
        If mode Is Nothing Then mode = "Open"
        If Not node.EnableTls AndAlso mode = "Disabled" Then mode = "Open"
        node.HttpMode = mode
    End Sub

    ''' <summary>
    ''' The node rebases a relative DataDirectory against its own
    ''' AppContext.BaseDirectory (NodeConfiguration); mirror that here so
    ''' the cert lands where the node will look, not under setup's cwd.
    ''' </summary>
    Public Function ResolveDataDirectory(cfg As NodeSetupConfig) As String
        Dim d = If(cfg?.Node?.DataDirectory, "")
        If String.IsNullOrWhiteSpace(d) Then d = NodeSection.DefaultDataDirectory()
        If Not Path.IsPathRooted(d) Then d = Path.Combine(AppContext.BaseDirectory, d)
        Return Path.GetFullPath(d)
    End Function

    ''' <summary>
    ''' Mints (or loads) the node's self-signed certificate using the SAME
    ''' service the node runs, so the fingerprint printed here is the one
    ''' the Manager will pin. Returns the bare-hex SHA-256 fingerprint, or
    ''' Nothing with <paramref name="errorMessage"/> set on failure. Never
    ''' throws.
    ''' </summary>
    Public Function EnsureTlsCertificate(cfg As NodeSetupConfig, ByRef errorMessage As String) As String
        errorMessage = Nothing
        Try
            Dim svc As New GSM.Node.Security.TlsCertificateService(ResolveDataDirectory(cfg))
            Dim cert = svc.GetOrCreateCertificate(cfg.Node.NodeId)
            Return GSM.Node.Security.TlsCertificateService.FingerprintSha256(cert)
        Catch ex As Exception
            errorMessage = ex.Message
            Return Nothing
        End Try
    End Function

    ''' <summary>Fingerprint of the cert already on disk, or Nothing if none.</summary>
    Public Function ReadTlsFingerprint(cfg As NodeSetupConfig) As String
        Try
            Dim svc As New GSM.Node.Security.TlsCertificateService(ResolveDataDirectory(cfg))
            Return svc.TryReadPersistedFingerprint()
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>Deletes and re-mints the cert. Returns the new fingerprint or Nothing (+ error).</summary>
    Public Function RegenerateTlsCertificate(cfg As NodeSetupConfig, ByRef errorMessage As String) As String
        errorMessage = Nothing
        Try
            Dim svc As New GSM.Node.Security.TlsCertificateService(ResolveDataDirectory(cfg))
            Dim cert = svc.RegenerateCertificate(cfg.Node.NodeId)
            Return GSM.Node.Security.TlsCertificateService.FingerprintSha256(cert)
        Catch ex As Exception
            errorMessage = ex.Message
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' The "paste into the Manager" block printed after a save / auto-init:
    ''' token, fingerprint (when HTTPS is on) and the URL shape.
    ''' </summary>
    Public Function BuildManagerHandoffLines(cfg As NodeSetupConfig, fingerprint As String) As String()
        Dim lines As New System.Collections.Generic.List(Of String)
        lines.Add("Auth token:                 " & cfg.Node.AuthToken)
        If cfg.Node.EnableTls Then
            If Not String.IsNullOrEmpty(fingerprint) Then
                lines.Add("TLS fingerprint (SHA-256):  " & fingerprint)
                lines.Add("                            (paste into Manager -> Add Node -> Cert SHA-256)")
            Else
                lines.Add("TLS fingerprint (SHA-256):  (not available - see error above)")
            End If
            Dim tlsPort = If(cfg.Node.TlsPort > 0, cfg.Node.TlsPort, cfg.Node.ListenPort)
            Dim plain As String
            Select Case NormalizeHttpMode(cfg.Node.HttpMode)
                Case "Disabled" : plain = "disabled"
                Case "LoopbackOnly" : plain = "localhost only"
                Case Else : plain = "open on port " & cfg.Node.ListenPort
            End Select
            lines.Add($"Listens on:                 https://<host>:{tlsPort}  (plain HTTP: {plain})")
        Else
            lines.Add($"Listens on:                 http://<host>:{cfg.Node.ListenPort}  (HTTPS off - use 'Secure this node' in the Manager later)")
        End If
        Return lines.ToArray()
    End Function

    ''' <summary>
    ''' True when the running process is on Windows. Centralized so we
    ''' do not scatter OperatingSystem.IsWindows() checks throughout
    ''' the UI files.
    '''
    ''' The SupportedOSPlatformGuard attribute teaches the platform-
    ''' compatibility analyzer that an `If RunningOnWindows() Then ...`
    ''' block is a valid Windows-only guard. Without it, the analyzer
    ''' raises CA1416 on Windows-only API calls inside the block (such
    ''' as the WindowsIdentity / WindowsPrincipal calls in
    ''' RunningElevated below), because it only recognizes the literal
    ''' OperatingSystem.IsWindows() call directly.
    ''' </summary>
    <SupportedOSPlatformGuard("windows")>
    Public Function RunningOnWindows() As Boolean
        Return OperatingSystem.IsWindows()
    End Function

    ''' <summary>
    ''' True when the current process is running with elevated
    ''' privileges. On Windows that means the user is in the
    ''' Administrators group with elevation; on Linux that means
    ''' running as uid 0 (root). Used to decide whether to attempt
    ''' a service install vs. printing instructions.
    ''' </summary>
    Public Function RunningElevated() As Boolean
        Try
            If RunningOnWindows() Then
                Using identity = Security.Principal.WindowsIdentity.GetCurrent()
                    Dim principal As New Security.Principal.WindowsPrincipal(identity)
                    Return principal.IsInRole(Security.Principal.WindowsBuiltInRole.Administrator)
                End Using
            Else
                ' On Unix-like systems geteuid() == 0 is the universal
                ' definition of root. Environment.UserName == "root" is
                ' a reasonable heuristic when we cannot P/Invoke.
                Return String.Equals(Environment.UserName, "root", StringComparison.Ordinal)
            End If
        Catch
            Return False
        End Try
    End Function

End Module
