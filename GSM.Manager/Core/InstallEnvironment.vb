Imports System
Imports System.IO

Namespace GSM.Manager.Core

    ''' <summary>
    ''' Phase 5l-1 — facts about where the Manager is installed that
    ''' the self-update flow needs. Class with Shared members (not a
    ''' Module) so the generic-sounding names aren't hoisted into
    ''' namespace scope.
    ''' </summary>
    Public Class InstallEnvironment

        ''' <summary>The directory the running Manager lives in.</summary>
        Public Shared Function InstallDirectory() As String
            Return AppContext.BaseDirectory
        End Function

        ''' <summary>
        ''' Whether the install directory is writable by this process —
        ''' a cheap proxy for "can self-update swap the binaries here".
        ''' Mechanism (per Phase 5l Decision 5): create then delete a
        ''' uniquely-named temp file in the install dir. Microseconds
        ''' cheap, and avoids the more invasive "overwrite the exe with
        ''' itself" probe that could trip antivirus. Catches the
        ''' Program-Files-without-elevation, read-only-share, and
        ''' Controlled-Folder-Access cases.
        ''' </summary>
        Public Shared Function IsInstallWritable() As Boolean
            Try
                Dim dir = InstallDirectory()
                If String.IsNullOrEmpty(dir) Then Return False
                Dim probe = Path.Combine(dir, ".write-probe-" & Guid.NewGuid().ToString("N") & ".tmp")
                File.WriteAllText(probe, "probe")
                Try
                    File.Delete(probe)
                Catch
                    ' Wrote but couldn't delete — still effectively
                    ' writable for our purposes; leave the stray file.
                End Try
                Return True
            Catch
                Return False
            End Try
        End Function

    End Class

End Namespace
