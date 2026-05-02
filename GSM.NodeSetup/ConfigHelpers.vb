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
