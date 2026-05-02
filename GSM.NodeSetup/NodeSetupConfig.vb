Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization

' ============================================================
'  NodeSetupConfig — strongly-typed mirror of nodesettings.json
'
'  This class is the single source of truth for the schema the
'  setup tool reads and writes. The shape MUST match what
'  GSM.Node.NodeProgram binds via builder.Configuration.Bind().
'
'  Read-modify-write pattern:
'    Dim cfg = NodeSetupConfig.LoadOrCreate(path)
'    cfg.Node.ListenPort = 8765
'    cfg.Save(path, backupExisting:=True)
'
'  When the file does not exist, LoadOrCreate returns a populated
'  default with NodeId = Environment.MachineName and the auth-token
'  placeholder so the tool can show a "needs setup" status.
' ============================================================

Public Class NodeSetupConfig

    Public Property Node As New NodeSection()
    Public Property Security As New SecuritySection()
    Public Property Logging As New LoggingSection()

    Public Const PlaceholderToken As String = "CHANGE_ME_BEFORE_FIRST_RUN"

    ''' <summary>
    ''' Loads from the given path or returns a populated default.
    ''' Never throws on a missing file — that is the "first run" state.
    ''' Throws on a malformed file because silent corruption recovery
    ''' would mask the user's misconfiguration.
    ''' </summary>
    Public Shared Function LoadOrCreate(path As String) As NodeSetupConfig
        If String.IsNullOrEmpty(path) Then
            Throw New ArgumentException("Config path is required.", NameOf(path))
        End If

        If Not File.Exists(path) Then
            Dim fresh As New NodeSetupConfig()
            fresh.Node.NodeId = Environment.MachineName
            Return fresh
        End If

        Dim json = File.ReadAllText(path, Encoding.UTF8)
        If String.IsNullOrWhiteSpace(json) Then
            ' Treat empty file the same as missing file.
            Dim fresh As New NodeSetupConfig()
            fresh.Node.NodeId = Environment.MachineName
            Return fresh
        End If

        Dim opts As New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True,
            .ReadCommentHandling = JsonCommentHandling.Skip,
            .AllowTrailingCommas = True
        }

        Dim loaded = JsonSerializer.Deserialize(Of NodeSetupConfig)(json, opts)
        If loaded Is Nothing Then
            Throw New InvalidDataException("Failed to parse " & path)
        End If

        ' Defensive: nothing in the JSON section deserializer guarantees
        ' the nested objects exist. Re-create any nulls so callers never
        ' have to null-check.
        If loaded.Node Is Nothing Then loaded.Node = New NodeSection()
        If loaded.Security Is Nothing Then loaded.Security = New SecuritySection()
        If loaded.Logging Is Nothing Then loaded.Logging = New LoggingSection()
        If loaded.Logging.LogLevel Is Nothing Then
            loaded.Logging.LogLevel = LoggingSection.DefaultLogLevels()
        End If

        Return loaded
    End Function

    ''' <summary>
    ''' Writes the configuration to disk as indented JSON. When
    ''' backupExisting is True and a file already exists at the path,
    ''' it is copied to <path>.bak first (overwriting any prior backup).
    ''' Atomic write via temp file + rename to avoid a half-written
    ''' settings file if the process is killed mid-write.
    '''
    ''' Note: the parameter is named filePath rather than path because
    ''' VB.Net is case-insensitive and a parameter named "path" would
    ''' shadow the System.IO.Path class (Path.GetDirectoryName below).
    ''' </summary>
    Public Sub Save(filePath As String, Optional backupExisting As Boolean = True)
        If String.IsNullOrEmpty(filePath) Then
            Throw New ArgumentException("Config path is required.", NameOf(filePath))
        End If

        Dim dir = Path.GetDirectoryName(Path.GetFullPath(filePath))
        If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
            Directory.CreateDirectory(dir)
        End If

        If backupExisting AndAlso File.Exists(filePath) Then
            Try
                File.Copy(filePath, filePath & ".bak", overwrite:=True)
            Catch
                ' Backup is best-effort. Better to save a working file
                ' than to refuse the save because we couldn't make a copy.
            End Try
        End If

        Dim opts As New JsonSerializerOptions With {
            .WriteIndented = True,
            .DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            .Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }

        Dim json = JsonSerializer.Serialize(Me, opts)

        Dim tempPath = filePath & ".tmp"
        File.WriteAllText(tempPath, json, New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))

        ' File.Replace would be ideal but it requires the destination to
        ' exist. File.Move with overwrite handles both the new-file and
        ' replace-existing cases atomically on .NET 8.
        If File.Exists(filePath) Then
            File.Move(tempPath, filePath, overwrite:=True)
        Else
            File.Move(tempPath, filePath)
        End If
    End Sub

    ''' <summary>
    ''' True when the auth token is missing or still the literal
    ''' placeholder value the setup tool ships with. The Manager
    ''' cannot connect with this value, so the tool surfaces it as
    ''' a "needs setup" status.
    '''
    ''' JsonIgnore: this is a computed property over Node.AuthToken,
    ''' not a stored field. Without the attribute, System.Text.Json
    ''' serializes it into the output file (--auto-init produced a
    ''' "NeedsAuthTokenSetup": false line that did nothing on read
    ''' because the property is read-only).
    ''' </summary>
    <JsonIgnore>
    Public ReadOnly Property NeedsAuthTokenSetup As Boolean
        Get
            Return ConfigHelpers.IsAuthTokenPlaceholder(Node?.AuthToken)
        End Get
    End Property

End Class

Public Class NodeSection
    Public Property NodeId As String = ""
    Public Property ListenPort As Integer = 8765
    Public Property AuthToken As String = NodeSetupConfig.PlaceholderToken
    Public Property DataDirectory As String = "./data"
    ''' <summary>
    ''' Default parent directory for new game-server installations.
    ''' Mirrors NodeConfiguration.ServersDirectory in GSM.Node — the
    ''' two MUST stay in sync because builder.Configuration.Bind binds
    ''' the JSON file directly into the runtime class.
    '''
    ''' Default "./servers" resolves to a folder next to the node
    ''' executable at runtime (NodeConfiguration.EnsureDefaults
    ''' rebases relative paths against AppContext.BaseDirectory).
    ''' Operators routinely override this when game files belong on
    ''' a separate volume from the node binary.
    ''' </summary>
    Public Property ServersDirectory As String = "./servers"
    Public Property MaxConcurrentInstalls As Integer = 2
    Public Property LogRetentionDays As Integer = 30
    Public Property MetricsIntervalSeconds As Integer = 5
End Class

Public Class SecuritySection
    Public Property MaxFailedAttempts As Integer = 10
    Public Property FailureWindowMinutes As Integer = 5
    Public Property LockoutMinutes As Integer = 15
    Public Property AuthFailureDelayMs As Integer = 250
    Public Property RequestsPerMinutePerIp As Integer = 600
    Public Property MaxRequestBodyBytes As Long = 4194304L
    Public Property MaxConcurrentConnections As Integer = 100
End Class

Public Class LoggingSection
    Public Property LogLevel As Dictionary(Of String, String) = DefaultLogLevels()

    Public Shared Function DefaultLogLevels() As Dictionary(Of String, String)
        Return New Dictionary(Of String, String) From {
            {"Default", "Information"},
            {"Microsoft.AspNetCore", "Warning"}
        }
    End Function
End Class
