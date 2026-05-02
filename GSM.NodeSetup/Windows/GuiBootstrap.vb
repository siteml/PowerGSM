Imports System
Imports System.Windows.Forms

' ============================================================
'  GuiBootstrap — WinForms entry point. Compiled only on the
'                 net8.0-windows TFM (the Windows\ folder is
'                 excluded from the cross-platform build via
'                 the project file).
'
'  Kept tiny so the GUI form file (MainSetupForm.vb) holds all
'  the actual UI code and this just owns Application.Run.
' ============================================================

Namespace Windows

    Public Module GuiBootstrap

        Public Sub Run(configPath As String)
            ' .NET 8 WinForms initialization. The C# SDK source-generates
            ' an ApplicationConfiguration.Initialize() helper that calls
            ' these three methods in this exact order, but the VB.Net
            ' SDK does NOT generate that helper, so we make the calls
            ' ourselves. Skipping them gives a low-DPI, classic-themed,
            ' visually broken form on Windows 10 / 11.
            Application.SetHighDpiMode(HighDpiMode.SystemAware)
            Application.EnableVisualStyles()
            Application.SetCompatibleTextRenderingDefault(False)

            Using form As New MainSetupForm(configPath)
                Application.Run(form)
            End Using
        End Sub

    End Module

End Namespace
