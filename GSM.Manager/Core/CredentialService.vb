Imports System
Imports System.Security.Cryptography
Imports System.Text
Imports System.Linq
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data
Imports GSM.Node.Api

' ============================================================
'  CredentialService — manages Steam and realm credentials
'
'  Passwords are encrypted at rest using Windows DPAPI
'  (DataProtectionScope.CurrentUser). The encrypted bytes are
'  stored in SQLite. Only the Windows account that encrypted
'  them can decrypt — copying the database to another machine
'  or user renders the passwords unrecoverable.
'
'  Credentials are decrypted transiently for install operations
'  and never written to disk in cleartext.
' ============================================================

Namespace GSM.Manager.Core

    Public Class CredentialService

        Private ReadOnly _logger As ILogger(Of CredentialService)

        Public Sub New(logger As ILogger(Of CredentialService))
            _logger = logger
        End Sub

        ' ============================================================
        '  Steam credentials
        ' ============================================================

        ''' <summary>
        ''' Saves or updates a Steam credential. Password is encrypted
        ''' with DPAPI before storage.
        ''' </summary>
        Public Sub SaveSteamCredential(db As GsmDbContext,
                                       credentialId As String,
                                       displayName As String,
                                       username As String,
                                       password As String,
                                       isAnonymous As Boolean)

            Dim existing = db.SteamCredentials.Find(credentialId)
            If existing Is Nothing Then
                existing = New SteamCredentialEntity With {
                    .CredentialId = credentialId
                }
                db.SteamCredentials.Add(existing)
            End If

            existing.DisplayName = displayName
            existing.Username = username
            existing.IsAnonymous = isAnonymous

            If isAnonymous Then
                existing.EncryptedPassword = Array.Empty(Of Byte)()
            Else
                existing.EncryptedPassword = EncryptString(password)
            End If

            db.SaveChanges()
            _logger.LogInformation("Saved Steam credential '{Name}' ({Id})",
                                   displayName, credentialId)
        End Sub

        ''' <summary>
        ''' Decrypts and returns a SteamCredential DTO ready to send
        ''' to a node for an install operation. The password exists
        ''' only in memory and is never persisted in cleartext.
        ''' </summary>
        Public Function GetSteamCredentialForTransmit(db As GsmDbContext,
                                                      credentialId As String) As SteamCredential
            Dim entity = db.SteamCredentials.Find(credentialId)
            If entity Is Nothing Then Return Nothing

            Dim cred As New SteamCredential With {
                .Username = entity.Username,
                .IsAnonymous = entity.IsAnonymous
            }

            If Not entity.IsAnonymous AndAlso
               entity.EncryptedPassword IsNot Nothing AndAlso
               entity.EncryptedPassword.Length > 0 Then
                cred.Password = DecryptString(entity.EncryptedPassword)
            End If

            Return cred
        End Function

        ''' <summary>
        ''' Deletes a Steam credential.
        ''' </summary>
        Public Sub DeleteSteamCredential(db As GsmDbContext, credentialId As String)
            Dim entity = db.SteamCredentials.Find(credentialId)
            If entity IsNot Nothing Then
                db.SteamCredentials.Remove(entity)
                db.SaveChanges()
            End If
        End Sub

        ''' <summary>
        ''' Returns all Steam credentials (without decrypting passwords).
        ''' </summary>
        Public Function ListSteamCredentials(db As GsmDbContext) As List(Of SteamCredentialEntity)
            Return db.SteamCredentials.ToList()
        End Function

        ' ============================================================
        '  Realm credentials
        ' ============================================================

        Public Sub SaveRealmCredential(db As GsmDbContext,
                                       credentialId As String,
                                       displayName As String,
                                       gameId As String,
                                       customerKey As String,
                                       providerKey As String)

            Dim existing = db.RealmCredentials.Find(credentialId)
            If existing Is Nothing Then
                existing = New RealmCredentialEntity With {
                    .CredentialId = credentialId
                }
                db.RealmCredentials.Add(existing)
            End If

            existing.DisplayName = displayName
            existing.GameId = gameId
            existing.EncryptedCustomerKey = EncryptString(customerKey)
            existing.EncryptedProviderKey = EncryptString(providerKey)

            db.SaveChanges()
        End Sub

        Public Function DecryptRealmKeys(entity As RealmCredentialEntity) As (CustomerKey As String, ProviderKey As String)
            Dim ck = If(entity.EncryptedCustomerKey IsNot Nothing AndAlso entity.EncryptedCustomerKey.Length > 0,
                        DecryptString(entity.EncryptedCustomerKey), "")
            Dim pk = If(entity.EncryptedProviderKey IsNot Nothing AndAlso entity.EncryptedProviderKey.Length > 0,
                        DecryptString(entity.EncryptedProviderKey), "")
            Return (ck, pk)
        End Function

        ' ============================================================
        '  DPAPI encryption helpers
        ' ============================================================

        ''' <summary>
        ''' Encrypts a string using DPAPI (CurrentUser scope).
        ''' </summary>
        Private Shared Function EncryptString(plainText As String) As Byte()
            If String.IsNullOrEmpty(plainText) Then Return Array.Empty(Of Byte)()
            Dim plainBytes = Encoding.UTF8.GetBytes(plainText)
            Return ProtectedData.Protect(plainBytes, Nothing,
                                         DataProtectionScope.CurrentUser)
        End Function

        ''' <summary>
        ''' Decrypts DPAPI-protected bytes back to a string.
        ''' </summary>
        Private Shared Function DecryptString(encryptedBytes As Byte()) As String
            If encryptedBytes Is Nothing OrElse encryptedBytes.Length = 0 Then Return ""
            Dim plainBytes = ProtectedData.Unprotect(encryptedBytes, Nothing,
                                                      DataProtectionScope.CurrentUser)
            Return Encoding.UTF8.GetString(plainBytes)
        End Function

    End Class

End Namespace
