Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
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
    ''' Unknown-key preservation (slice 0 of HTTPS_SinglePort_Plan): the
    ''' node itself writes keys into this file that the setup tool does
    ''' not model (historically EnableTls / TlsPort / HttpMode via
    ''' NodeSettingsFile, plus DisableShim). Serializing the typed object
    ''' alone silently dropped them — editing any setting through this
    ''' tool after HTTPS onboarding put the node back on plain HTTP at its
    ''' next restart. The typed object is now serialized to a JsonNode and
    ''' MERGED over the existing file: every key present on disk that the
    ''' typed model does not know about (top-level sections and keys inside
    ''' known sections, matched case-insensitively) is carried through
    ''' verbatim. Known keys always take the typed value.
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

        Dim typedNode = JsonSerializer.SerializeToNode(Me, opts)?.AsObject()
        If typedNode Is Nothing Then
            Throw New InvalidOperationException("Failed to serialize node configuration.")
        End If

        Dim existingNode As JsonObject = Nothing
        If File.Exists(filePath) Then
            Try
                Dim existingJson = File.ReadAllText(filePath, Encoding.UTF8)
                If Not String.IsNullOrWhiteSpace(existingJson) Then
                    Dim docOpts As New JsonDocumentOptions With {
                        .CommentHandling = JsonCommentHandling.Skip,
                        .AllowTrailingCommas = True
                    }
                    existingNode = TryCast(JsonNode.Parse(existingJson, Nothing, docOpts), JsonObject)
                End If
            Catch
                ' Unparseable existing file: LoadOrCreate would already have
                ' thrown for the caller's read, so this is the fresh-file
                ' path in practice. Write the typed model as-is rather than
                ' refusing to save.
                existingNode = Nothing
            End Try
        End If

        If existingNode IsNot Nothing Then
            MergeUnknownKeys(existingNode, typedNode)
        End If

        Dim json = typedNode.ToJsonString(opts)

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
    ''' Copies every key from <paramref name="source"/> that has no
    ''' case-insensitive counterpart in <paramref name="target"/> into
    ''' target. Recurses one level into sections that are objects on both
    ''' sides so node-written keys inside "Node" survive. Keys the typed
    ''' model knows are never touched — the typed value wins.
    ''' </summary>
    Private Shared Sub MergeUnknownKeys(source As JsonObject, target As JsonObject)
        For Each kv In source
            Dim existingKey = FindKeyIgnoreCase(target, kv.Key)
            If existingKey Is Nothing Then
                ' Unknown at this level: carry through verbatim (deep clone —
                ' a JsonNode can only have one parent).
                target(kv.Key) = If(kv.Value Is Nothing, Nothing, kv.Value.DeepClone())
            Else
                Dim srcObj = TryCast(kv.Value, JsonObject)
                Dim dstObj = TryCast(target(existingKey), JsonObject)
                If srcObj IsNot Nothing AndAlso dstObj IsNot Nothing Then
                    MergeUnknownKeys(srcObj, dstObj)
                End If
            End If
        Next
    End Sub

    Private Shared Function FindKeyIgnoreCase(obj As JsonObject, key As String) As String
        For Each kv In obj
            If String.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) Then
                Return kv.Key
            End If
        Next
        Return Nothing
    End Function

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
    ''' <summary>
    ''' Default data directory — absolute path next to the executable.
    ''' Computed at construction time rather than as the literal string
    ''' "./data" because relative paths bite operators when the node runs
    ''' as a service: systemd's working directory is not the install
    ''' folder, so "./data" would resolve under /etc/systemd/system or
    ''' wherever systemctl was invoked from. The node's runtime layer
    ''' rebases relative paths against AppContext.BaseDirectory as a
    ''' fallback, but writing the absolute path into the config from
    ''' day one means there's nothing magic happening at runtime and
    ''' the operator can read the JSON file and know exactly where data
    ''' lives without inspecting code.
    ''' </summary>
    Public Property DataDirectory As String = DefaultDataDirectory()
    ''' <summary>
    ''' Default parent directory for new game-server installations.
    ''' Mirrors NodeConfiguration.ServersDirectory in GSM.Node — the
    ''' two MUST stay in sync because builder.Configuration.Bind binds
    ''' the JSON file directly into the runtime class.
    '''
    ''' See DataDirectory's comment for why this is absolute by default.
    ''' Operators routinely override this when game files belong on
    ''' a separate volume from the node binary.
    ''' </summary>
    Public Property ServersDirectory As String = DefaultServersDirectory()
    Public Property MaxConcurrentInstalls As Integer = 2
    Public Property LogRetentionDays As Integer = 30
    Public Property MetricsIntervalSeconds As Integer = 5

    ' ---- Transport (0.7.0 HTTPS; mirrors NodeConfiguration in GSM.Node) ----
    ' These were absent from this model until HTTPS_SinglePort_Plan slice 0,
    ' which meant Save() stripped them. Defaults MUST equal the node's
    ' NodeConfiguration defaults so a file written by an older setup tool
    ' round-trips to the same runtime behaviour.

    ''' <summary>Serve HTTPS using the node's self-signed certificate.</summary>
    Public Property EnableTls As Boolean = False

    ''' <summary>
    ''' HTTPS port. 0 = node default. Pre-single-port nodes resolve 0 to
    ''' ListenPort + 1; single-port nodes resolve 0 to ListenPort itself.
    ''' A non-zero value opens a dedicated HTTPS listener on that port.
    ''' </summary>
    Public Property TlsPort As Integer = 0

    ''' <summary>
    ''' Plain-HTTP policy: "Open", "LoopbackOnly" or "Disabled". The node
    ''' coerces Disabled back to Open when EnableTls is off.
    ''' </summary>
    Public Property HttpMode As String = "Open"

    ''' <summary>
    ''' Absolute path to the default data directory — a sibling "data"
    ''' folder next to the executable. Used as the property initializer
    ''' default and exposed for the wizard so prompts and validation
    ''' agree on what the default looks like.
    ''' </summary>
    Public Shared Function DefaultDataDirectory() As String
        Return Path.Combine(AppContext.BaseDirectory, "data")
    End Function

    ''' <summary>
    ''' Absolute path to the default servers directory — sibling "servers"
    ''' folder next to the executable. See DefaultDataDirectory.
    ''' </summary>
    Public Shared Function DefaultServersDirectory() As String
        Return Path.Combine(AppContext.BaseDirectory, "servers")
    End Function
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
