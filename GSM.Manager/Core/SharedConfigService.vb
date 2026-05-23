Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.Json
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin
Imports GSM.Manager.Data

' ============================================================
'  SharedConfigService — CRUD + encryption for the Phase 5h
'  shared-config-groups feature.
'
'  Companion to CredentialService (which manages Steam
'  credentials and exposes the DPAPI Protect/Unprotect
'  helpers). Where CredentialService stores a single
'  fixed-schema "Steam credential" concept that any SteamCMD
'  plugin can reference, SharedConfigService stores
'  plugin-defined groups whose schemas come from
'  ISharedConfigProvider.GetSharedConfigSchema. The wire
'  shape on disk is a single ConfigJson dictionary on the
'  SharedConfigGroupEntity row, with sensitive fields
'  encrypted in place via a sentinel-prefixed wrapper so a
'  single JSON column carries both plaintext and encrypted
'  values without a separate flag column or BLOB.
'
'  Phase 5h scope is CRUD + encryption only. The merge-into-
'  InstanceConfig.CustomFields step that makes plugin code
'  read group fields transparently lives in InstanceManager
'  (Phase 5h-2); the management UI and installation-editor
'  picker live in the WinForms layer (5h-4 / 5h-5).
' ============================================================

Namespace GSM.Manager.Core

    Public Class SharedConfigService

        Private ReadOnly _logger As ILogger(Of SharedConfigService)

        ''' <summary>
        ''' Sentinel prefix marking an encrypted value in the
        ''' stored JSON. A plaintext value that happened to start
        ''' with this exact 12-character prefix would collide,
        ''' which is verbose enough that accidental collision is
        ''' essentially impossible. If a stored value DOES start
        ''' with this prefix and the rest isn't valid base64-of-
        ''' DPAPI-bytes, decryption surfaces the failure to the
        ''' logger and the caller receives an empty string for
        ''' that field — visible misbehaviour rather than silent
        ''' substitution of garbage.
        ''' </summary>
        Private Const EncryptedSentinel As String = "__GSM_ENC__:"

        Public Sub New(logger As ILogger(Of SharedConfigService))
            _logger = logger
        End Sub

        ' ============================================================
        '  CRUD
        ' ============================================================

        ''' <summary>
        ''' Lists all groups belonging to a given plugin + group
        ''' type. Used by management UI to populate pickers and
        ''' the installation editor's "pick existing realm"
        ''' dropdown.
        ''' </summary>
        Public Function ListGroups(db As GsmDbContext,
                                   pluginId As String,
                                   groupType As String) As IReadOnlyList(Of SharedConfigGroupEntity)
            Return db.SharedConfigGroups.
                Where(Function(g) g.PluginId = pluginId AndAlso g.GroupType = groupType).
                OrderBy(Function(g) g.DisplayName).
                ToList()
        End Function

        ''' <summary>
        ''' Fetches one group by id. Returns Nothing if not found.
        ''' </summary>
        Public Function GetGroup(db As GsmDbContext, groupId As String) As SharedConfigGroupEntity
            If String.IsNullOrEmpty(groupId) Then Return Nothing
            Return db.SharedConfigGroups.Find(groupId)
        End Function

        ''' <summary>
        ''' Creates a new group. plaintextFields contains user-
        ''' supplied values; sensitive fields (per schema) are
        ''' encrypted before storage. Returns the generated
        ''' GroupId. The caller is expected to manage the
        ''' InstallationEntity.SharedConfigGroupId assignment as
        ''' a separate step.
        ''' </summary>
        Public Function CreateGroup(db As GsmDbContext,
                                    pluginId As String,
                                    groupType As String,
                                    displayName As String,
                                    plaintextFields As Dictionary(Of String, String),
                                    schema As IReadOnlyList(Of ConfigFieldDescriptor)) As String
            Dim now = DateTime.UtcNow
            Dim entity As New SharedConfigGroupEntity With {
                .GroupId = Guid.NewGuid().ToString(),
                .PluginId = pluginId,
                .GroupType = groupType,
                .DisplayName = displayName,
                .ConfigJson = SerialiseFieldsForStorage(plaintextFields, schema),
                .CreatedUtc = now,
                .UpdatedUtc = now
            }
            db.SharedConfigGroups.Add(entity)
            db.SaveChanges()
            _logger.LogInformation(
                "Created shared-config group '{Name}' ({Id}) for {Plugin}/{Type}",
                displayName, entity.GroupId, pluginId, groupType)
            Return entity.GroupId
        End Function

        ''' <summary>
        ''' Updates an existing group's display name and fields.
        ''' Sensitive fields re-encrypt per the supplied schema.
        ''' Throws InvalidOperationException if the group is gone.
        ''' </summary>
        Public Sub UpdateGroup(db As GsmDbContext,
                               groupId As String,
                               displayName As String,
                               plaintextFields As Dictionary(Of String, String),
                               schema As IReadOnlyList(Of ConfigFieldDescriptor))
            Dim entity = db.SharedConfigGroups.Find(groupId)
            If entity Is Nothing Then
                Throw New InvalidOperationException(
                    $"Shared-config group {groupId} not found")
            End If
            entity.DisplayName = displayName
            entity.ConfigJson = SerialiseFieldsForStorage(plaintextFields, schema)
            entity.UpdatedUtc = DateTime.UtcNow
            db.SaveChanges()
            _logger.LogInformation(
                "Updated shared-config group '{Name}' ({Id})",
                displayName, groupId)
        End Sub

        ''' <summary>
        ''' Deletes a group. EF Core's ClientSetNull default for
        ''' optional FKs handles dependent installations: their
        ''' SharedConfigGroupId becomes NULL rather than cascading
        ''' deletion. No-op if the group doesn't exist.
        ''' </summary>
        Public Sub DeleteGroup(db As GsmDbContext, groupId As String)
            Dim entity = db.SharedConfigGroups.Find(groupId)
            If entity IsNot Nothing Then
                db.SharedConfigGroups.Remove(entity)
                db.SaveChanges()
                _logger.LogInformation("Deleted shared-config group {Id}", groupId)
            End If
        End Sub

        ' ============================================================
        '  Plaintext access — for plugin invocation and editors
        ' ============================================================

        ''' <summary>
        ''' Returns the group's fields in plaintext (sensitive
        ''' fields decrypted). Schema is required so this method
        ''' knows which fields need decryption — passing the
        ''' wrong schema returns plaintext-for-sensitive-fields
        ''' as the encrypted-sentinel-prefixed string instead of
        ''' the real value (which is a clear signal that the
        ''' caller hasn't fetched the schema correctly).
        '''
        ''' Returns an empty dictionary if the group isn't found
        ''' or has no fields, rather than Nothing, so callers can
        ''' chain into .ContainsKey / .TryGetValue without a null
        ''' check first.
        ''' </summary>
        Public Function LoadGroupFieldsPlaintext(db As GsmDbContext,
                                                 groupId As String,
                                                 schema As IReadOnlyList(Of ConfigFieldDescriptor)) As Dictionary(Of String, String)
            Dim entity = GetGroup(db, groupId)
            If entity Is Nothing Then
                Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            End If
            Return DeserialiseFieldsFromStorage(entity.ConfigJson, schema)
        End Function

        ' ============================================================
        '  Encryption sentinel encode/decode
        '
        '  The on-disk JSON dictionary holds mixed plaintext and
        '  encrypted values. Encrypted values are prefixed with
        '  EncryptedSentinel followed by base64 of the DPAPI-
        '  protected bytes. The sentinel makes the distinction
        '  unambiguous without a separate flag column.
        ' ============================================================

        Private Function SerialiseFieldsForStorage(plaintext As Dictionary(Of String, String),
                                                   schema As IReadOnlyList(Of ConfigFieldDescriptor)) As String
            If plaintext Is Nothing Then
                plaintext = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            End If

            Dim sensitiveKeys = BuildSensitiveKeySet(schema)
            Dim storage As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

            For Each kvp In plaintext
                If String.IsNullOrEmpty(kvp.Value) Then
                    ' Empty input round-trips as empty regardless
                    ' of sensitivity. Encrypting an empty string
                    ' would produce a fixed-size envelope that
                    ' leaks "yes, this was set, just to empty",
                    ' which is worse than just storing empty.
                    storage(kvp.Key) = ""
                ElseIf sensitiveKeys.Contains(kvp.Key) Then
                    Dim encryptedBytes = CredentialService.ProtectString(kvp.Value)
                    storage(kvp.Key) = EncryptedSentinel & Convert.ToBase64String(encryptedBytes)
                Else
                    storage(kvp.Key) = kvp.Value
                End If
            Next

            Return JsonSerializer.Serialize(storage)
        End Function

        Private Function DeserialiseFieldsFromStorage(json As String,
                                                      schema As IReadOnlyList(Of ConfigFieldDescriptor)) As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If String.IsNullOrEmpty(json) Then Return result

            Dim raw As Dictionary(Of String, String) = Nothing
            Try
                raw = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
            Catch ex As Exception
                _logger.LogWarning(ex, "Failed to deserialise shared-config JSON; returning empty fields")
                Return result
            End Try
            If raw Is Nothing Then Return result

            For Each kvp In raw
                If String.IsNullOrEmpty(kvp.Value) Then
                    result(kvp.Key) = ""
                ElseIf kvp.Value.StartsWith(EncryptedSentinel, StringComparison.Ordinal) Then
                    Try
                        Dim b64 = kvp.Value.Substring(EncryptedSentinel.Length)
                        Dim encryptedBytes = Convert.FromBase64String(b64)
                        result(kvp.Key) = CredentialService.UnprotectString(encryptedBytes)
                    Catch ex As Exception
                        _logger.LogWarning(
                            ex,
                            "Failed to decrypt sensitive shared-config field '{Key}'; field will be empty in returned dict",
                            kvp.Key)
                        result(kvp.Key) = ""
                    End Try
                Else
                    result(kvp.Key) = kvp.Value
                End If
            Next

            Return result
        End Function

        Private Shared Function BuildSensitiveKeySet(schema As IReadOnlyList(Of ConfigFieldDescriptor)) As HashSet(Of String)
            Dim keys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            If schema Is Nothing Then Return keys
            For Each descriptor In schema
                If descriptor.IsSensitive AndAlso Not String.IsNullOrEmpty(descriptor.Key) Then
                    keys.Add(descriptor.Key)
                End If
            Next
            Return keys
        End Function

    End Class

End Namespace
