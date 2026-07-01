Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.Web.WebView2.Core
Imports Microsoft.Web.WebView2.WinForms
Imports GSM.Utility

' ============================================================
'  WebSessionCaptureForm — Phase 7-3 round 2 (Decision 7a)
'
'  Manager-owned embedded-browser login for utility plugins.
'  The plugin calls IUtilityContext.CaptureWebSessionAsync; this
'  form shows the target site in WebView2, the user performs the
'  REAL login (genuine portal, genuine Steam Guard, password
'  manager autofill all intact — PowerGSM never sees credentials
'  and automates nothing), and once navigation reaches a URL
'  containing the completion pattern, the session cookies for
'  the requested domain are harvested via
'  CoreWebView2.CookieManager.GetCookiesAsync — which reads the
'  browser's cookie jar directly and therefore captures HttpOnly
'  cookies (the decisive advantage over any JS-injection
'  approach, since session cookies are typically HttpOnly).
'
'  Threading: CaptureAsync spins a dedicated STA thread and runs
'  the dialog modally there (ShowDialog pumps messages), so the
'  call is safe from any thread — including a utility plugin's
'  drain loop — and never blocks the Manager's main UI thread.
'
'  Requires the WebView2 Evergreen Runtime; when absent, the
'  result degrades to a clear "runtime missing" error carrying
'  the download URL instead of crashing.
'
'  Browser state lives in a dedicated, wipeable per-plugin
'  user-data folder (WebView2Data\{pluginId} next to the exe) —
'  deleting it forgets every embedded-browser login.
' ============================================================

Namespace GSM.Manager.UI

    Public Class WebSessionCaptureForm
        Inherits Form

        Private ReadOnly _pluginId As String
        Private ReadOnly _startUrl As String
        Private ReadOnly _completionUrlPattern As String
        Private ReadOnly _cookieDomain As String

        Private _webView As WebView2
        Private _statusLabel As Label
        Private _completed As Boolean

        ''' <summary>The capture outcome; populated by the time the
        ''' dialog closes (cancel = Ok:=False).</summary>
        Public Property Result As WebSessionCaptureResult

        ''' <summary>
        ''' Entry point — safe to call from any thread. Shows the
        ''' capture dialog on a dedicated STA thread and completes
        ''' when it closes.
        ''' </summary>
        Public Shared Function CaptureAsync(pluginId As String,
                                            startUrl As String,
                                            completionUrlPattern As String,
                                            cookieDomain As String) As Task(Of WebSessionCaptureResult)
            Dim tcs As New TaskCompletionSource(Of WebSessionCaptureResult)(
                TaskCreationOptions.RunContinuationsAsynchronously)

            If String.IsNullOrWhiteSpace(startUrl) OrElse
               String.IsNullOrWhiteSpace(completionUrlPattern) OrElse
               String.IsNullOrWhiteSpace(cookieDomain) Then
                tcs.SetResult(New WebSessionCaptureResult With {
                    .Ok = False,
                    .ErrorMessage = "CaptureWebSessionAsync requires startUrl, completionUrlPattern, and cookieDomain."})
                Return tcs.Task
            End If

            Dim staThread As New Thread(
                Sub()
                    Try
                        Using frm As New WebSessionCaptureForm(pluginId, startUrl, completionUrlPattern, cookieDomain)
                            frm.ShowDialog()
                            tcs.TrySetResult(If(frm.Result, New WebSessionCaptureResult With {
                                .Ok = False, .ErrorMessage = "Capture dialog closed without a result."}))
                        End Using
                    Catch ex As Exception
                        tcs.TrySetResult(New WebSessionCaptureResult With {
                            .Ok = False, .ErrorMessage = $"Capture dialog failed: {ex.Message}"})
                    End Try
                End Sub)
            staThread.SetApartmentState(ApartmentState.STA)
            staThread.IsBackground = True
            staThread.Name = $"WebCapture-{pluginId}"
            staThread.Start()

            Return tcs.Task
        End Function

        Public Sub New(pluginId As String, startUrl As String,
                       completionUrlPattern As String, cookieDomain As String)
            _pluginId = pluginId
            _startUrl = startUrl
            _completionUrlPattern = completionUrlPattern
            _cookieDomain = cookieDomain

            FormIconHelper.ApplyTo(Me)
            Me.Text = $"Log in — requested by plugin '{pluginId}'"
            Me.Size = New Size(980, 760)
            Me.StartPosition = FormStartPosition.CenterScreen
            Me.MinimizeBox = False

            Dim bottomStrip As New Panel With {.Dock = DockStyle.Bottom, .Height = 42}
            _statusLabel = New Label With {
                .Text = "Starting embedded browser…",
                .AutoSize = True, .Location = New Point(10, 12)}
            bottomStrip.Controls.Add(_statusLabel)

            Dim cancelButton As New Button With {
                .Text = "Cancel", .Size = New Size(90, 28),
                .Location = New Point(Me.ClientSize.Width - 110, 7),
                .Anchor = AnchorStyles.Top Or AnchorStyles.Right}
            AddHandler cancelButton.Click, Sub(s, e) Me.Close()
            bottomStrip.Controls.Add(cancelButton)
            Me.Controls.Add(bottomStrip)

            _webView = New WebView2 With {.Dock = DockStyle.Fill}
            Me.Controls.Add(_webView)
            _webView.BringToFront()

            AddHandler Me.Load, AddressOf OnDialogLoad
            AddHandler Me.FormClosing, AddressOf OnDialogClosing
        End Sub

        ''' <summary>Named OnDialogLoad, not OnLoad — OnLoad shadows
        ''' Form.OnLoad (known landmine).</summary>
        Private Async Sub OnDialogLoad(sender As Object, e As EventArgs)
            Try
                ' Dedicated, wipeable per-plugin browser profile.
                Dim userDataFolder = Path.Combine(AppContext.BaseDirectory,
                                                  "WebView2Data", _pluginId)
                Directory.CreateDirectory(userDataFolder)

                Dim env = Await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder:=Nothing,
                    userDataFolder:=userDataFolder)
                Await _webView.EnsureCoreWebView2Async(env)

                AddHandler _webView.CoreWebView2.SourceChanged,
                    Async Sub(s2, e2) Await CheckForCompletionAsync()
                AddHandler _webView.CoreWebView2.NavigationCompleted,
                    Async Sub(s2, e2) Await CheckForCompletionAsync()

                _statusLabel.Text = $"Log in normally. This window closes itself once you reach …{_completionUrlPattern}…"
                _webView.CoreWebView2.Navigate(_startUrl)

            Catch ex As WebView2RuntimeNotFoundException
                Result = New WebSessionCaptureResult With {
                    .Ok = False,
                    .ErrorMessage = "The WebView2 Runtime isn't installed on this machine. " &
                                    "Install the Evergreen Runtime from " &
                                    "https://developer.microsoft.com/microsoft-edge/webview2/ and retry."}
                MessageBox.Show(Me, Result.ErrorMessage, "Embedded Browser Unavailable",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                _completed = True
                Me.Close()
            Catch ex As Exception
                Result = New WebSessionCaptureResult With {
                    .Ok = False,
                    .ErrorMessage = $"Embedded browser failed to start: {ex.Message}"}
                _completed = True
                Me.Close()
            End Try
        End Sub

        ''' <summary>
        ''' Runs on every navigation/source change; when the URL
        ''' contains the completion pattern, harvests the cookies for
        ''' the requested domain and closes with success. Guarded so
        ''' overlapping events can't double-harvest.
        ''' </summary>
        Private Async Function CheckForCompletionAsync() As Task
            If _completed Then Return
            Try
                Dim currentUrl = If(_webView.CoreWebView2?.Source, "")
                If currentUrl.IndexOf(_completionUrlPattern, StringComparison.OrdinalIgnoreCase) < 0 Then Return
                _completed = True

                _statusLabel.Text = "Login detected — capturing session…"

                Dim cookieUrl = _cookieDomain
                If Not cookieUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                    cookieUrl = "https://" & cookieUrl
                End If

                Dim rawCookies = Await _webView.CoreWebView2.CookieManager.GetCookiesAsync(cookieUrl)
                Dim captured As New List(Of CapturedCookie)
                If rawCookies IsNot Nothing Then
                    For Each c In rawCookies
                        captured.Add(New CapturedCookie With {
                            .Name = c.Name,
                            .Value = c.Value,
                            .Domain = c.Domain,
                            .Path = c.Path,
                            .ExpiresUtc = If(c.IsSession, CType(Nothing, DateTime?), c.Expires.ToUniversalTime()),
                            .IsHttpOnly = c.IsHttpOnly,
                            .IsSecure = c.IsSecure
                        })
                    Next
                End If

                Result = New WebSessionCaptureResult With {.Ok = True, .CompletionUrl = currentUrl}
                Result.Cookies.AddRange(captured)
                Me.Close()
            Catch ex As Exception
                Result = New WebSessionCaptureResult With {
                    .Ok = False,
                    .ErrorMessage = $"Cookie capture failed: {ex.Message}"}
                Me.Close()
            End Try
        End Function

        ''' <summary>Named OnDialogClosing, not OnFormClosing —
        ''' shadowing landmine again. Closing without a result =
        ''' the user cancelled.</summary>
        Private Sub OnDialogClosing(sender As Object, e As FormClosingEventArgs)
            If Result Is Nothing Then
                Result = New WebSessionCaptureResult With {
                    .Ok = False,
                    .ErrorMessage = "The login window was closed before the login completed."}
            End If
        End Sub

    End Class

End Namespace
