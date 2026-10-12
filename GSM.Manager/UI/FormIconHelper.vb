Imports System
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

' ============================================================
'  FormIconHelper — centralised access to the PowerGSM icon,
'  plus the screen-fit clamp every form inherits through it.
'
'  The .ico file is embedded in the Manager assembly as
'  "PowerGSM.ico" (see the RootNamespace-less project setup
'  that maps filenames to resource names verbatim). Every
'  form in the Manager calls ApplyTo(Me) in its constructor
'  so the title bar and Alt-Tab preview are branded
'  consistently across the whole app.
'
'  Because EVERY form funnels through ApplyTo, it is also the
'  one place to guarantee forms actually FIT the display: on
'  Load, each form is clamped to its screen's WorkingArea
'  (screen minus taskbar). Several dialogs carry fixed design
'  heights up to ~785 px — fine on 1080p, but on a 1280x800
'  panel (~750 px usable) their bottom buttons landed under or
'  below the taskbar with no way to reach them on a
'  FixedDialog. The clamp is a no-op on screens big enough for
'  the design size, so normal layouts are untouched; on small
'  screens the form shrinks to fit, fixed-border dialogs get
'  AutoScroll so clipped content stays reachable, and the
'  window is nudged fully on-screen.
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

            ' Screen-fit clamp (see file header). Hooked on Load — the
            ' form's Size is assigned after ApplyTo runs in the ctor, and
            ' by Load the target screen is known, so that's the earliest
            ' correct moment to measure and clamp.
            Try
                AddHandler form.Load, AddressOf ClampToWorkingAreaOnLoad
            Catch
            End Try

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
        ''' Clamp a form to its screen's WorkingArea (screen minus
        ''' taskbar) and keep it fully on-screen. No-op when the form
        ''' already fits — large screens see zero change. When a
        ''' fixed-border dialog has to shrink, AutoScroll is enabled so
        ''' the content below the fold (typically the Save/Cancel row of
        ''' an absolutely-laid-out dialog) is reachable by scrolling
        ''' instead of stranded off-screen. MinimumSize is relaxed first
        ''' where it would defeat the clamp.
        ''' </summary>
        Private Sub ClampToWorkingAreaOnLoad(sender As Object, e As EventArgs)
            Dim form = TryCast(sender, Form)
            If form Is Nothing Then Return
            Try
                Dim wa = Screen.FromControl(form).WorkingArea
                Dim newW = Math.Min(form.Width, wa.Width)
                Dim newH = Math.Min(form.Height, wa.Height)

                If newW < form.Width OrElse newH < form.Height Then
                    Select Case form.FormBorderStyle
                        Case FormBorderStyle.FixedDialog,
                             FormBorderStyle.FixedSingle,
                             FormBorderStyle.Fixed3D,
                             FormBorderStyle.FixedToolWindow
                            ' The user can't resize their way out — make
                            ' the clipped remainder scrollable.
                            form.AutoScroll = True
                    End Select
                    If form.MinimumSize.Width > newW OrElse form.MinimumSize.Height > newH Then
                        form.MinimumSize = New Size(
                            Math.Min(form.MinimumSize.Width, newW),
                            Math.Min(form.MinimumSize.Height, newH))
                    End If
                    form.Size = New Size(newW, newH)
                End If

                ' Nudge fully into the working area (a centered-on-parent
                ' dialog whose parent sits low can still hang off the
                ' bottom even after the size clamp).
                Dim x = Math.Max(wa.Left, Math.Min(form.Left, wa.Right - form.Width))
                Dim y = Math.Max(wa.Top, Math.Min(form.Top, wa.Bottom - form.Height))
                If x <> form.Left OrElse y <> form.Top Then
                    form.Location = New Point(x, y)
                End If
            Catch
                ' Sizing is best-effort — never break a form over it.
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
