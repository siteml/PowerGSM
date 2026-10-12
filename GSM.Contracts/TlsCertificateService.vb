Imports System
Imports System.IO
Imports System.Net
Imports System.Security.Cryptography
Imports System.Security.Cryptography.X509Certificates

' ============================================================
'  TlsCertificateService — self-signed node certificate (0.7.0)
'
'  Lives in GSM.Contracts (HTTPS_SinglePort_Plan slice 2) so BOTH the
'  node (serves it) and NodeSetup (mints it at setup time and prints
'  the fingerprint next to the auth token) use one implementation —
'  same paths, same key-storage flags, same fingerprint. Contracts is
'  dependency-free, so logging is two optional callbacks instead of
'  ILogger; the node wires them to its logger, NodeSetup to the console.
'
'  Trust model: the node generates one long-lived self-signed
'  certificate; the Manager pins its SHA-256 fingerprint (TOFU,
'  like SSH host keys). Chain and hostname are deliberately NOT
'  part of the trust decision — the pin is the trust anchor —
'  so the cert's subject/SAN content is cosmetic.
'
'  Persistence: PFX at <DataDirectory>/tls/node.pfx with a
'  random password stored beside it (node.pfx.pwd). The private
'  key never leaves this machine; the pwd file only defends
'  against casual copy of the pfx alone, and gets 0600 perms on
'  Linux. The box already runs game servers under this account —
'  the pfx is not a higher-value target than the process itself.
' ============================================================

Namespace GSM.Node.Security

    ''' <summary>
    ''' Owns generation, persistence, and loading of the node's
    ''' self-signed TLS certificate. In the node it is a DI singleton
    ''' constructed eagerly in NodeProgram (before host build) when TLS
    ''' is enabled so Kestrel can bind the listener, and used lazily by
    ''' the /api/tls/enable endpoint otherwise. NodeSetup constructs a
    ''' throwaway instance to mint / inspect / regenerate the cert.
    ''' </summary>
    Public Class TlsCertificateService

        Private Const CertValidYears As Integer = 10

        Private ReadOnly _dataDirectory As String
        Private ReadOnly _logInfo As Action(Of String)
        Private ReadOnly _logWarn As Action(Of String, Exception)
        Private ReadOnly _gate As New Object()
        Private _cert As X509Certificate2

        ''' <param name="dataDirectory">The node's DataDirectory; the cert lives in its tls\ subfolder.</param>
        ''' <param name="logInfo">Optional informational sink (generation events).</param>
        ''' <param name="logWarn">Optional warning sink (expiry / unloadable cert); exception may be Nothing.</param>
        Public Sub New(dataDirectory As String,
                       Optional logInfo As Action(Of String) = Nothing,
                       Optional logWarn As Action(Of String, Exception) = Nothing)
            _dataDirectory = dataDirectory
            _logInfo = logInfo
            _logWarn = logWarn
        End Sub

        ''' <summary>Folder holding node.pfx / node.pfx.pwd (NodeSetup chowns it on Linux).</summary>
        Public ReadOnly Property TlsDirectory As String
            Get
                Return Path.Combine(_dataDirectory, "tls")
            End Get
        End Property

        Private ReadOnly Property PfxPath As String
            Get
                Return Path.Combine(TlsDirectory, "node.pfx")
            End Get
        End Property

        Private ReadOnly Property PwdPath As String
            Get
                Return Path.Combine(TlsDirectory, "node.pfx.pwd")
            End Get
        End Property

        ''' <summary>True when a persisted certificate (pfx + pwd) exists on disk.</summary>
        Public ReadOnly Property HasPersistedCertificate As Boolean
            Get
                Return File.Exists(PfxPath) AndAlso File.Exists(PwdPath)
            End Get
        End Property

        ''' <summary>
        ''' Loads the persisted certificate, or generates + persists a
        ''' new one when absent or expired. Thread-safe; the loaded
        ''' instance is cached for the process lifetime. Throws on
        ''' unrecoverable crypto/IO failure — callers surface that as
        ''' CertGenerationFailed.
        ''' </summary>
        Public Function GetOrCreateCertificate(nodeId As String) As X509Certificate2
            SyncLock _gate
                If _cert IsNot Nothing Then Return _cert

                If HasPersistedCertificate Then
                    Try
                        Dim pwd = File.ReadAllText(PwdPath).Trim()
                        Dim loaded = LoadPfx(PfxPath, pwd)
                        If loaded.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30) Then
                            _cert = loaded
                            Return _cert
                        End If
                        ' Expired (or about to): regenerate. The Manager will
                        ' see a fingerprint change and require explicit
                        ' re-trust — log loudly so the operator can correlate.
                        Warn($"TLS certificate expired or expiring (NotAfter={loaded.NotAfter.ToUniversalTime():u}); generating a replacement. " &
                             "The Manager will report a fingerprint change that must be re-trusted.", Nothing)
                        loaded.Dispose()
                    Catch ex As Exception
                        Warn("Persisted TLS certificate could not be loaded; generating a replacement.", ex)
                    End Try
                End If

                _cert = GenerateAndPersist(nodeId)
                Return _cert
            End SyncLock
        End Function

        ''' <summary>
        ''' Deletes the persisted certificate and mints a fresh one
        ''' (NodeSetup "Regenerate TLS certificate"). Any Manager pinned
        ''' to the old fingerprint must re-trust. Returns the new cert.
        ''' </summary>
        Public Function RegenerateCertificate(nodeId As String) As X509Certificate2
            SyncLock _gate
                If _cert IsNot Nothing Then
                    Try
                        _cert.Dispose()
                    Catch
                    End Try
                    _cert = Nothing
                End If
                Try
                    If File.Exists(PfxPath) Then File.Delete(PfxPath)
                    If File.Exists(PwdPath) Then File.Delete(PwdPath)
                Catch ex As Exception
                    Warn("Could not remove the previous TLS certificate files; generating over them.", ex)
                End Try
                _cert = GenerateAndPersist(nodeId)
                Return _cert
            End SyncLock
        End Function

        ''' <summary>
        ''' Lowercase hex SHA-256 fingerprint of the current cert (loading
        ''' or generating it as needed), or Nothing on failure.
        ''' </summary>
        Public Function TryGetFingerprint(nodeId As String) As String
            Try
                Dim c = GetOrCreateCertificate(nodeId)
                Return FingerprintSha256(c)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Fingerprint of the PERSISTED cert without generating one when
        ''' absent (NodeSetup "Show TLS fingerprint"). Nothing when no cert
        ''' is on disk or it cannot be read.
        ''' </summary>
        Public Function TryReadPersistedFingerprint() As String
            If Not HasPersistedCertificate Then Return Nothing
            Try
                Dim pwd = File.ReadAllText(PwdPath).Trim()
                ' Ephemeral load is enough to hash the public cert on any OS.
                Using c As New X509Certificate2(PfxPath, pwd,
                        X509KeyStorageFlags.EphemeralKeySet)
                    Return FingerprintSha256(c)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Public Shared Function FingerprintSha256(cert As X509Certificate2) As String
            Return Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant()
        End Function

        ''' <summary>"ab12:cd34:…" grouping for human display; input is the bare hex form.</summary>
        Public Shared Function FormatFingerprintForDisplay(hex As String) As String
            If String.IsNullOrEmpty(hex) Then Return ""
            Dim sb As New Text.StringBuilder(hex.Length + hex.Length \ 4)
            For i = 0 To hex.Length - 1
                If i > 0 AndAlso i Mod 4 = 0 Then sb.Append(":"c)
                sb.Append(hex(i))
            Next
            Return sb.ToString()
        End Function

        Private Function GenerateAndPersist(nodeId As String) As X509Certificate2
            Directory.CreateDirectory(TlsDirectory)

            Dim cn = If(String.IsNullOrWhiteSpace(nodeId), Environment.MachineName, nodeId)

            Using key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                Dim req As New CertificateRequest(
                    "CN=" & cn, key, HashAlgorithmName.SHA256)

                req.CertificateExtensions.Add(
                    New X509BasicConstraintsExtension(False, False, 0, False))
                req.CertificateExtensions.Add(
                    New X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, False))
                req.CertificateExtensions.Add(
                    New X509EnhancedKeyUsageExtension(
                        New OidCollection From {New Oid("1.3.6.1.5.5.7.3.1")}, False)) ' serverAuth

                ' SAN is cosmetic under fingerprint pinning, but browsers /
                ' curl used against the node appreciate it. Best effort.
                Try
                    Dim san As New SubjectAlternativeNameBuilder()
                    san.AddDnsName(Environment.MachineName)
                    san.AddIpAddress(IPAddress.Loopback)
                    Try
                        Dim host = Dns.GetHostEntry(Dns.GetHostName())
                        For Each addr In host.AddressList
                            If addr.AddressFamily = Sockets.AddressFamily.InterNetwork Then
                                san.AddIpAddress(addr)
                            End If
                        Next
                    Catch
                    End Try
                    req.CertificateExtensions.Add(san.Build())
                Catch
                End Try

                Dim notBefore = DateTimeOffset.UtcNow.AddDays(-1)
                Dim notAfter = DateTimeOffset.UtcNow.AddYears(CertValidYears)

                Using generated = req.CreateSelfSigned(notBefore, notAfter)
                    ' Random password; stored beside the pfx (see class header
                    ' for the honest threat-model statement on this).
                    Dim pwdBytes(31) As Byte
                    RandomNumberGenerator.Fill(pwdBytes)
                    Dim pwd = Convert.ToBase64String(pwdBytes)

                    File.WriteAllBytes(PfxPath, generated.Export(X509ContentType.Pfx, pwd))
                    File.WriteAllText(PwdPath, pwd)
                    RestrictPermissions(PfxPath)
                    RestrictPermissions(PwdPath)

                    ' Re-import so the private key is usable by Kestrel on
                    ' Windows (CreateSelfSigned yields an ephemeral key that
                    ' SChannel refuses; the PFX round-trip with a PERSISTED
                    ' key store is the canonical fix — see LoadPfx).
                    Dim usable = LoadPfx(PfxPath, pwd)

                    Info($"Generated self-signed TLS certificate CN={cn} NotAfter={notAfter.UtcDateTime:u} FingerprintSHA256={FingerprintSha256(usable)}")

                    Return usable
                End Using
            End Using
        End Function

        ''' <summary>
        ''' Loads the PFX with key-storage flags that actually work for
        ''' serving TLS on each OS.
        '''
        ''' WINDOWS: SChannel (what Kestrel uses) CANNOT use private keys
        ''' imported with EphemeralKeySet — handshakes fail with a
        ''' credential error while the process otherwise looks healthy.
        ''' The key must land in a persisted CNG store. Preference order:
        '''   1. MachineKeySet — right choice for a service (LocalSystem /
        '''      service accounts often have no loaded user profile). This
        '''      is also what lets NodeSetup (run as an admin user) mint a
        '''      cert the service account can later use.
        '''   2. UserKeySet — fallback for a bare, non-admin console run.
        ''' LINUX: OpenSSL has no such restriction; EphemeralKeySet keeps
        ''' the key out of the filesystem keystore entirely.
        ''' </summary>
        Private Shared Function LoadPfx(path As String, pwd As String) As X509Certificate2
            If Not OperatingSystem.IsWindows() Then
                Return New X509Certificate2(path, pwd,
                    X509KeyStorageFlags.Exportable Or X509KeyStorageFlags.EphemeralKeySet)
            End If
            Try
                Return New X509Certificate2(path, pwd,
                    X509KeyStorageFlags.Exportable Or
                    X509KeyStorageFlags.MachineKeySet Or
                    X509KeyStorageFlags.PersistKeySet)
            Catch
                ' No rights to the machine store (bare non-admin run) —
                ' fall back to the per-user store.
                Return New X509Certificate2(path, pwd,
                    X509KeyStorageFlags.Exportable Or
                    X509KeyStorageFlags.UserKeySet Or
                    X509KeyStorageFlags.PersistKeySet)
            End Try
        End Function

        ''' <summary>chmod 600 on Linux; no-op elsewhere (NTFS ACLs of the
        ''' service account's profile dir are the Windows story).</summary>
        Private Shared Sub RestrictPermissions(path As String)
            If OperatingSystem.IsWindows() Then Return
            Try
                File.SetUnixFileMode(path, UnixFileMode.UserRead Or UnixFileMode.UserWrite)
            Catch
                ' Best effort.
            End Try
        End Sub

        Private Sub Info(message As String)
            Try
                _logInfo?.Invoke(message)
            Catch
            End Try
        End Sub

        Private Sub Warn(message As String, ex As Exception)
            Try
                _logWarn?.Invoke(message, ex)
            Catch
            End Try
        End Sub

    End Class

End Namespace
