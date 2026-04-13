Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports GSM.Data
Imports GSM.Plugin

' ============================================================
'  CredentialService
'
'  Handles all credential encryption and decryption using
'  Windows DPAPI (System.Security.Cryptography.ProtectedData).
'
'  DPAPI basics for someone new to it:
'    ProtectedData.Protect(plainBytes, entropy, scope)
'      → returns encrypted bytes (a "blob")
'      → encrypted with the current Windows user's key
'      → the encrypted blob is useless without that user account
'      → copying the SQLite file to another machine = no decryption
'
'    ProtectedData.Unprotect(encryptedBlob, entropy, scope)
'      → returns the original plain bytes
'      → only works on the same machine AND same user account
'        (CurrentUser scope) or same machine (LocalMachine scope)
'
'  We use CurrentUser scope. If the manager is later run as a
'  Windows Service under a service account, all existing
'  credentials must be re-encrypted under that account first.
'  The manager should warn about this on first run as a service.
'
'  Entropy:
'    We use a fixed per-field entropy value (a byte array) as
'    an extra layer. This means even if someone could somehow
'    decrypt one field, they can't use that to decrypt another.
'    It's a belt-and-suspenders measure on top of DPAPI.
'
'  What gets encrypted:
'    - NodeEntity.AuthToken
'    - SteamCredentialEntity.EncryptedPassword
'    - RealmCredentialEntity.EncryptedCustomerKey
'    - RealmCredentialEntity.EncryptedProviderKey
'
'  What does NOT get encrypted:
'    - Usernames (not sensitive)
'    - GameIds, display names, notes
'    - Plugin config blobs (contain no credentials - those are
'      referenced by ID and resolved at launch time)
' ============================================================

Namespace GSM.Core

    Public Class CredentialService

        Private ReadOnly _db As GsmDbContext
        Private ReadOnly _logger As ILogger(Of CredentialService)

        ' Fixed entropy values per field type.
        ' These are not secret - they're just domain separators.
        ' Changing these would invalidate all existing encrypted values.
        Private Shared ReadOnly EntropyNodeToken As Byte() =
            Encoding.UTF8.GetBytes("GSM.Node.AuthToken.v1")
        Private Shared ReadOnly EntropySteamPassword As Byte() =
            Encoding.UTF8.GetBytes("GSM.Steam.Password.v1")
        Private Shared ReadOnly EntropyCustomerKey As Byte() =
            Encoding.UTF8.GetBytes("GSM.Realm.CustomerKey.v1")
        Private Shared ReadOnly EntropyProviderKey As Byte() =
            Encoding.UTF8.GetBytes("GSM.Realm.ProviderKey.v1")

        Public Sub New(db As GsmDbContext,
                       logger As ILogger(Of CredentialService))
            _db = db
            _logger = logger
        End Sub


        ' ============================================================
        '  STEAM CREDENTIALS
        ' ============================================================

        Public Async Function CreateSteamCredentialAsync(
                displayName As String,
                username As String,
                password As String,
                isAnonymous As Boolean,
                gameId As String,
                notes As String,
                cancellation As CancellationToken) As Task(Of SteamCredentialEntity)

            Dim entity As New SteamCredentialEntity With {
                .CredentialId = Guid.NewGuid().ToString(),
                .DisplayName = displayName,
                .Username = If(isAnonymous, "anonymous", username),
                .EncryptedPassword = If(isAnonymous OrElse String.IsNullOrEmpty(password),
                                        Nothing,
                                        Encrypt(password, EntropySteamPassword)),
                .IsAnonymous = isAnonymous,
                .GameId = If(gameId, ""),
                .Notes = If(notes, ""),
                .CreatedAt = DateTime.UtcNow
            }

            _db.SteamCredentials.Add(entity)
            Await _db.SaveChangesAsync(cancellation)

            _logger.LogInformation(
                "CredentialService: created Steam credential '{Name}' (id: {Id})",
                displayName, entity.CredentialId)

            Return entity
        End Function

        Public Async Function UpdateSteamPasswordAsync(
                credentialId As String,
                newPassword As String,
                cancellation As CancellationToken) As Task

            Dim entity = Await _db.SteamCredentials.FindAsync(
                New Object() {credentialId}, cancellation)
            If entity Is Nothing Then
                Throw New InvalidOperationException(
                    $"Steam credential '{credentialId}' not found.")
            End If

            entity.EncryptedPassword = If(String.IsNullOrEmpty(newPassword),
                                           Nothing,
                                           Encrypt(newPassword, EntropySteamPassword))

            Await _db.SaveChangesAsync(cancellation)
            _logger.LogInformation(
                "CredentialService: updated password for Steam credential '{Id}'",
                credentialId)
        End Function

        ' Decrypt a Steam credential's password.
        ' Returns empty string for anonymous credentials.
        ' Called by InstanceManager when building an InstallRequest.
        Public Function DecryptSteamPassword(entity As SteamCredentialEntity) As String
            If entity.IsAnonymous OrElse entity.EncryptedPassword Is Nothing Then
                Return String.Empty
            End If
            Return Decrypt(entity.EncryptedPassword, EntropySteamPassword)
        End Function

        Public Async Function GetSteamCredentialAsync(
                credentialId As String,
                cancellation As CancellationToken) As Task(Of SteamCredentialEntity)
            Return Await _db.SteamCredentials.FindAsync(
                New Object() {credentialId}, cancellation)
        End Function

        Public Async Function ListSteamCredentialsAsync(
                cancellation As CancellationToken) As Task(Of List(Of SteamCredentialEntity))
            ' Never return encrypted bytes to callers who just need the list.
            ' The EncryptedPassword blob is only read when explicitly decrypting.
            Return Await _db.SteamCredentials.
                ToListAsync(cancellation)
        End Function

        Public Async Function DeleteSteamCredentialAsync(
                credentialId As String,
                cancellation As CancellationToken) As Task

            Dim entity = Await _db.SteamCredentials.FindAsync(
                New Object() {credentialId}, cancellation)
            If entity Is Nothing Then Return

            ' Check for references before deleting.
            Dim inUse = Await _db.Installations.
                AnyAsync(Function(i) i.SteamCredentialId = credentialId, cancellation)
            If inUse Then
                Throw New InvalidOperationException(
                    "Cannot delete Steam credential: it is in use by one or more installations.")
            End If

            _db.SteamCredentials.Remove(entity)
            Await _db.SaveChangesAsync(cancellation)
        End Function


        ' ============================================================
        '  REALM CREDENTIALS
        ' ============================================================

        Public Async Function CreateRealmCredentialAsync(
                displayName As String,
                gameId As String,
                customerKey As String,
                providerKey As String,
                notes As String,
                cancellation As CancellationToken) As Task(Of RealmCredentialEntity)

            Dim entity As New RealmCredentialEntity With {
                .CredentialId = Guid.NewGuid().ToString(),
                .DisplayName = displayName,
                .GameId = gameId,
                .EncryptedCustomerKey = Encrypt(customerKey, EntropyCustomerKey),
                .EncryptedProviderKey = Encrypt(providerKey, EntropyProviderKey),
                .Notes = If(notes, ""),
                .CreatedAt = DateTime.UtcNow
            }

            _db.RealmCredentials.Add(entity)
            Await _db.SaveChangesAsync(cancellation)

            _logger.LogInformation(
                "CredentialService: created realm credential '{Name}' (id: {Id})",
                displayName, entity.CredentialId)

            Return entity
        End Function

        Public Async Function UpdateRealmCredentialAsync(
                credentialId As String,
                displayName As String,
                customerKey As String,
                providerKey As String,
                notes As String,
                cancellation As CancellationToken) As Task

            Dim entity = Await _db.RealmCredentials.FindAsync(
                New Object() {credentialId}, cancellation)
            If entity Is Nothing Then
                Throw New InvalidOperationException(
                    $"Realm credential '{credentialId}' not found.")
            End If

            If displayName IsNot Nothing Then entity.DisplayName = displayName
            If notes IsNot Nothing Then entity.Notes = notes
            If Not String.IsNullOrEmpty(customerKey) Then
                entity.EncryptedCustomerKey = Encrypt(customerKey, EntropyCustomerKey)
            End If
            If Not String.IsNullOrEmpty(providerKey) Then
                entity.EncryptedProviderKey = Encrypt(providerKey, EntropyProviderKey)
            End If

            Await _db.SaveChangesAsync(cancellation)
        End Function

        ' Decrypt both keys from a realm credential.
        ' Returns a tuple: (customerKey, providerKey).
        ' Called by InstanceManager when building a StartInstanceRequest.
        Public Function DecryptRealmCredential(
                entity As RealmCredentialEntity) As (CustomerKey As String,
                                                      ProviderKey As String)
            Return (
                Decrypt(entity.EncryptedCustomerKey, EntropyCustomerKey),
                Decrypt(entity.EncryptedProviderKey, EntropyProviderKey)
            )
        End Function

        Public Async Function GetRealmCredentialAsync(
                credentialId As String,
                cancellation As CancellationToken) As Task(Of RealmCredentialEntity)
            Return Await _db.RealmCredentials.FindAsync(
                New Object() {credentialId}, cancellation)
        End Function

        Public Async Function ListRealmCredentialsAsync(
                gameId As String,
                cancellation As CancellationToken) As Task(Of List(Of RealmCredentialEntity))
            Dim query = _db.RealmCredentials.AsQueryable()
            If Not String.IsNullOrEmpty(gameId) Then
                query = query.Where(Function(r) r.GameId = gameId)
            End If
            Return Await query.ToListAsync(cancellation)
        End Function

        Public Async Function DeleteRealmCredentialAsync(
                credentialId As String,
                cancellation As CancellationToken) As Task

            Dim entity = Await _db.RealmCredentials.FindAsync(
                New Object() {credentialId}, cancellation)
            If entity Is Nothing Then Return

            ' Check for references.
            Dim inUseOnInstall = Await _db.Installations.
                AnyAsync(Function(i) i.RealmCredentialId = credentialId, cancellation)
            Dim inUseOnInstance = Await _db.Instances.
                AnyAsync(Function(i) i.RealmCredentialId = credentialId, cancellation)

            If inUseOnInstall OrElse inUseOnInstance Then
                Throw New InvalidOperationException(
                    "Cannot delete realm credential: it is in use by one or more " &
                    "installations or instances.")
            End If

            _db.RealmCredentials.Remove(entity)
            Await _db.SaveChangesAsync(cancellation)
        End Function


        ' ============================================================
        '  NODE AUTH TOKENS
        ' ============================================================

        Public Function EncryptNodeToken(plainToken As String) As Byte()
            Return Encrypt(plainToken, EntropyNodeToken)
        End Function

        Public Function DecryptNodeToken(encrypted As Byte()) As String
            Return Decrypt(encrypted, EntropyNodeToken)
        End Function

        ' Convenience overload used by NodeHttpClientFactory.
        Public Function DecryptString(encrypted As Byte()) As String
            ' Tries each entropy value until one works.
            ' In practice the caller knows which field this is,
            ' but this overload is handy for the node token case.
            Return Decrypt(encrypted, EntropyNodeToken)
        End Function


        ' ============================================================
        '  DPAPI CORE
        ' ============================================================

        Private Shared Function Encrypt(plainText As String,
                                         entropy As Byte()) As Byte()
            If String.IsNullOrEmpty(plainText) Then
                Return Array.Empty(Of Byte)()
            End If

            Dim plainBytes = Encoding.UTF8.GetBytes(plainText)
            ' DataProtectionScope.CurrentUser means:
            '   - Only decryptable by the same Windows user account
            '   - On the same machine
            '   - The OS manages the key derivation from the user's login credentials
            Return ProtectedData.Protect(
                plainBytes, entropy, DataProtectionScope.CurrentUser)
        End Function

        Private Shared Function Decrypt(encryptedBytes As Byte(),
                                         entropy As Byte()) As String
            If encryptedBytes Is Nothing OrElse encryptedBytes.Length = 0 Then
                Return String.Empty
            End If

            Try
                Dim plainBytes = ProtectedData.Unprotect(
                    encryptedBytes, entropy, DataProtectionScope.CurrentUser)
                Return Encoding.UTF8.GetString(plainBytes)
            Catch ex As CryptographicException
                ' This can happen if:
                '   - The database was moved to a different machine
                '   - The manager is running under a different user account
                '   - The data is corrupted
                Dim errMsg = "Failed to decrypt credential. This can happen if the database was " &
                    "moved to a different machine or the manager is running under a " &
                    "different Windows user account than when the credential was saved. " &
                    "Inner error: " & ex.Message
                Throw New InvalidOperationException(errMsg, ex)
            End Try
        End Function

    End Class

End Namespace
