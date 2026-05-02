Imports System
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

' ============================================================
'  FormIconHelper — centralised access to the PowerGSM icon.
'
'  The .ico file is embedded in the Manager assembly as
'  "PowerGSM.ico" (see the RootNamespace-less project setup
'  that maps filenames to resource names verbatim). Every
'  form in the Manager calls ApplyTo(Me) in its constructor
'  so the title bar and Alt-Tab preview are branded
'  consistently across the whole app.
'
'  The Welcome panel also uses GetLargeBitmap() to show the
'  256x256 variant as a logo next to the product name.
' ============================================================

Namespace GSM.Manager.UI

    Public Module FormIconHelper

        Private Const ResourceName As String = "PowerGSM.ico"

        ''' <summary>
        ''' Apply the PowerGSM icon to a form. Silently no-ops if
        ''' the resource is missing — never fail form construction
        ''' over a branding asset. A fresh Icon is created per call
        ''' so each form owns its own; no shared-lifetime hazards
        ''' when forms are disposed.
        ''' </summary>
        Public Sub ApplyTo(form As Form)
            If form Is Nothing Then Return
            Try
                Dim stream = OpenIconStream()
                If stream Is Nothing Then Return
                Using stream
                    form.Icon = New Icon(stream)
                End Using
            Catch
                ' Never let icon loading break UI.
            End Try
        End Sub

        ''' <summary>
        ''' Returns a freshly-loaded Bitmap of the icon rendered at
        ''' the best-available size close to 256x256. Used by the
        ''' Welcome panel as a logo. Ownership transfers to the
        ''' caller — wire into a PictureBox and dispose it when
        ''' the control goes away. Returns Nothing on error so
        ''' callers can fall back gracefully.
        ''' </summary>
        Public Function GetLargeBitmap() As Bitmap
            Try
                Dim stream = OpenIconStream()
                If stream Is Nothing Then Return Nothing
                Using stream
                    ' Icon(stream, w, h) picks the closest available
                    ' size in the .ico. If 256 isn't present, we get
                    ' the largest variant below it. ToBitmap preserves
                    ' the alpha channel for clean compositing.
                    Using big As New Icon(stream, 256, 256)
                        Return big.ToBitmap()
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Private Function OpenIconStream() As Stream
            ' GetType(FormIconHelper) works on Modules the same as on
            ' classes — the compiled type is the module's underlying
            ' NotInheritable Shared class.
            Return GetType(FormIconHelper).Assembly.
                GetManifestResourceStream(ResourceName)
        End Function

    End Module

End Namespace
