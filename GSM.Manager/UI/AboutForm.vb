Imports System
Imports System.Drawing
Imports System.Reflection
Imports System.Windows.Forms
Imports GSM.Node.Api

' ============================================================
'  AboutForm — Help → About PowerGSM dialog
'
'  Surfaces the three version axes (build, protocol, contracts)
'  documented in VERSIONING.md so users reporting issues can
'  cite a specific version and so the protocol/contracts
'  numbers are visible at a glance for compatibility checks.
'
'  Read-only, modal. Closes on OK or Esc.
' ============================================================

Namespace GSM.Manager.UI

    Public Class AboutForm
        Inherits Form

        ' PictureBox owns the logo bitmap. Tracked at class scope so
        ' Dispose can release it deterministically — GetLargeBitmap
        ' transfers ownership to us, and a modal dialog construc-
        ' ted/destructed every Help → About press would leak ~256KB
        ' of bitmap per click otherwise.
        Private _logoBox As PictureBox

        Public Sub New()
            FormIconHelper.ApplyTo(Me)
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()

            Me.Text = "About PowerGSM"
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.MaximizeBox = False
            Me.MinimizeBox = False
            Me.ShowInTaskbar = False
            Me.StartPosition = FormStartPosition.CenterParent
            ' Width grew from 420 to 480 to accommodate the logo on
            ' the left without crowding the version block on the
            ' right. Height stays the same; the logo is sized to
            ' fit comfortably within the existing vertical run.
            Me.ClientSize = New Size(480, 230)
            Me.Padding = New Padding(16)

            ' Read versions off our own assembly + the Contracts
            ' constants. ResolveBuildVersion strips the "+gitsha"
            ' suffix the SDK appends in source-linked builds so the
            ' headline reads cleanly; the suffix (if any) is shown
            ' separately on its own line so it stays diagnostic
            ' without cluttering the primary identity.
            Dim asm = Assembly.GetExecutingAssembly()
            Dim infoVersionRaw = ResolveInformationalVersion(asm)
            Dim build = StripGitSuffix(infoVersionRaw)
            Dim gitSuffix = ExtractGitSuffix(infoVersionRaw)

            ' ---- Logo ----
            ' Mirrors WelcomePanel's treatment so the Help → About
            ' dialog feels like the same brand surface. 96x96 fits
            ' the dialog's vertical run; SizeMode=Zoom keeps the
            ' aspect square regardless of which size variant the
            ' .ico happens to deliver.
            _logoBox = New PictureBox()
            _logoBox.Location = New Point(16, 16)
            _logoBox.Size = New Size(96, 96)
            _logoBox.SizeMode = PictureBoxSizeMode.Zoom
            ' GetLargeBitmap returns Nothing on resource failure;
            ' assigning Nothing to PictureBox.Image is harmless and
            ' just leaves an empty box. Better than crashing the
            ' About dialog over a missing branding asset.
            _logoBox.Image = FormIconHelper.GetLargeBitmap()
            Me.Controls.Add(_logoBox)

            ' Text-block X anchor — picks up immediately right of the
            ' logo with a comfortable gap. Original (logo-less)
            ' layout used 16; widening to 128 keeps the visual
            ' rhythm.
            Const textX As Integer = 128

            ' ---- Headline: PowerGSM <version> ----
            Dim titleLabel As New Label() With {
                .Text = "PowerGSM",
                .Font = New Font("Segoe UI", 16.0F, FontStyle.Bold),
                .AutoSize = True,
                .Location = New Point(textX, 16)
            }
            Me.Controls.Add(titleLabel)

            Dim versionLabel As New Label() With {
                .Text = $"Version {build}",
                .Font = New Font("Segoe UI", 11.0F),
                .AutoSize = True,
                .Location = New Point(textX, 52)
            }
            Me.Controls.Add(versionLabel)

            ' ---- Protocol / Contracts compatibility line ----
            Dim compatLabel As New Label() With {
                .Text = $"Protocol v{NodeApiContract.ProtocolVersion}    Contracts v{NodeApiContract.ContractsVersion}",
                .Font = New Font("Segoe UI", 9.0F),
                .ForeColor = SystemColors.GrayText,
                .AutoSize = True,
                .Location = New Point(textX, 82)
            }
            Me.Controls.Add(compatLabel)

            ' ---- Optional git-SHA line ----
            ' Only rendered when SourceRevisionId was populated
            ' during the build (non-null suffix). For a hand-built
            ' local debug build this line is absent and the dialog
            ' is shorter; for a CI / release build it's a useful
            ' identity anchor.
            Dim nextY = 112
            If Not String.IsNullOrEmpty(gitSuffix) Then
                Dim revLabel As New Label() With {
                    .Text = $"Build: {gitSuffix}",
                    .Font = New Font("Consolas", 8.5F),
                    .ForeColor = SystemColors.GrayText,
                    .AutoSize = True,
                    .Location = New Point(textX, nextY)
                }
                Me.Controls.Add(revLabel)
                nextY += 22
            End If

            ' ---- Description blurb ----
            ' Width set so it fits the right-hand text column rather
            ' than running under the logo on the next line.
            Dim blurbWidth = Me.ClientSize.Width - textX - 16
            Dim blurbLabel As New Label() With {
                .Text = "Multi-node Windows game server manager." & vbCrLf &
                        "See VERSIONING.md for the version policy.",
                .Font = New Font("Segoe UI", 9.0F),
                .AutoSize = False,
                .Size = New Size(blurbWidth, 36),
                .Location = New Point(textX, nextY)
            }
            Me.Controls.Add(blurbLabel)

            ' ---- OK button ----
            Dim okButton As New Button() With {
                .Text = "OK",
                .DialogResult = DialogResult.OK,
                .Size = New Size(80, 28),
                .Location = New Point(Me.ClientSize.Width - 16 - 80,
                                      Me.ClientSize.Height - 16 - 28),
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
            }
            Me.Controls.Add(okButton)
            Me.AcceptButton = okButton
            Me.CancelButton = okButton

        End Sub

        ' Dispose the logo bitmap explicitly so a series of Help →
        ' About / OK round trips doesn't accumulate native handles.
        ' GetLargeBitmap transferred ownership to us; PictureBox
        ' won't dispose its Image automatically.
        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing AndAlso _logoBox IsNot Nothing Then
                Dim img = _logoBox.Image
                _logoBox.Image = Nothing
                If img IsNot Nothing Then
                    Try
                        img.Dispose()
                    Catch
                    End Try
                End If
            End If
            MyBase.Dispose(disposing)
        End Sub

        ''' <summary>
        ''' Read AssemblyInformationalVersion. Falls back to
        ''' AssemblyVersion (3-part) when the attribute is absent
        ''' or empty. Returns "0.0.0" only as a last resort so
        ''' callers can always render something.
        ''' </summary>
        Private Function ResolveInformationalVersion(asm As Assembly) As String
            Try
                Dim attr = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
                If attr IsNot Nothing AndAlso
                   Not String.IsNullOrEmpty(attr.InformationalVersion) Then
                    Return attr.InformationalVersion
                End If
            Catch
                ' Reflection failure - fall through
            End Try

            Try
                Dim ver = asm.GetName().Version
                If ver IsNot Nothing Then Return ver.ToString(3)
            Catch
            End Try

            Return "0.0.0"
        End Function

        ''' <summary>
        ''' Returns the version string with any "+sha" suffix
        ''' removed. Example: "0.1.0+abc1234" -> "0.1.0".
        ''' </summary>
        Private Function StripGitSuffix(v As String) As String
            If String.IsNullOrEmpty(v) Then Return v
            Dim plus = v.IndexOf("+"c)
            If plus < 0 Then Return v
            Return v.Substring(0, plus)
        End Function

        ''' <summary>
        ''' Returns the "+sha" part (without the leading +) when
        ''' present, or empty string when absent.
        ''' </summary>
        Private Function ExtractGitSuffix(v As String) As String
            If String.IsNullOrEmpty(v) Then Return ""
            Dim plus = v.IndexOf("+"c)
            If plus < 0 OrElse plus = v.Length - 1 Then Return ""
            Return v.Substring(plus + 1)
        End Function

    End Class

End Namespace
