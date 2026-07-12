Imports System
Imports System.Collections.Generic
Imports GSM.Node.Api

' Microsoft.Win32 hosts the Registry API used to detect VC++
' redistributable installs. The types are Windows-specific in
' .NET 8 (analyzer warning CA1416), so every call site below
' guards with OperatingSystem.IsWindows() to keep cross-platform
' callers (Linux node) from throwing. The PackageReference to
' Microsoft.Win32.Registry is required for the import to resolve;
' see GSM.Node.vbproj.
Imports Microsoft.Win32

' ============================================================
'  PrerequisiteProbe — host-side runtime-dependency detection
'
'  Owns the catalog of known prerequisite names + the detection
'  logic for each. Returns enriched PrerequisiteCheckResult
'  records (with display metadata) so the Manager can render
'  pre-install notices without duplicating the catalog on its
'  side.
'
'  Adding a new prereq (e.g. DirectX runtime, .NET 6 desktop
'  runtime, OpenAL):
'    1. Add an entry to _catalog with DisplayName + DownloadUrl
'       + Instructions.
'    2. Add a Case clause in CheckSingle that calls a per-prereq
'       probe helper.
'    3. Implement the probe helper (ProbeVcRedistX64 below is
'       the reference). Helper returns (Installed, Version).
'
'  All detection runs on the node, where the runtime would
'  actually be installed. On platforms where a Windows-only
'  prereq is queried (e.g. vcredist-* on a Linux node) the
'  result is Recognized=True, Installed=False; the Manager
'  renders a notice that in practice only fires if the user is
'  trying to install a Windows-only game on a Linux node, which
'  the plugin's separate platform check would also catch (see
'  ConanExilesPlugin.GetPreInstallNotices).
' ============================================================

Namespace GSM.Node

    Public Class PrerequisiteProbe

        ''' <summary>
        ''' Catalog entry shape. Display fields are returned to the
        ''' Manager verbatim and rendered in the pre-install notice;
        ''' the detection runs out-of-band in CheckSingle keyed off
        ''' the catalog key (the prereq name).
        ''' </summary>
        Private Class CatalogEntry
            Public DisplayName As String
            Public DownloadUrl As String
            Public Instructions As String
        End Class

        ''' <summary>
        ''' Static catalog. Case-insensitive on the key (prereq name)
        ''' so a plugin can send "VCRedist-2015-2022-x64" or
        ''' "vcredist-2015-2022-x64" interchangeably; canonical form
        ''' is lowercase kebab-case.
        '''
        ''' To add a new prereq, append here AND add a matching
        ''' Case clause in CheckSingle pointing at a per-prereq probe
        ''' helper. The two halves stay in sync via the same
        ''' lowercased string.
        ''' </summary>
        Private Shared ReadOnly _catalog As New Dictionary(Of String, CatalogEntry)(
            StringComparer.OrdinalIgnoreCase) From {
            {"vcredist-2015-2022-x64", New CatalogEntry With {
                .DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
                .DownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                .Instructions = "This game requires the Microsoft Visual C++ runtime to launch. " &
                                 "Download and run the installer linked below on this node, then " &
                                 "retry the installation. Without the runtime the game crashes " &
                                 "silently at startup (typically exit code -1073741515 / " &
                                 "STATUS_DLL_NOT_FOUND, with no log produced)."
            }},
            {"linux-xvfb", New CatalogEntry With {
                .DisplayName = "Xvfb virtual framebuffer (Linux)",
                .DownloadUrl = "",
                .Instructions = "This game needs a display to initialise its graphics device, " &
                                 "and headless servers have none — Xvfb provides a virtual one. " &
                                 "Install it on the node with: sudo apt install xvfb " &
                                 "(Debian/Ubuntu) or the equivalent for your distribution, then " &
                                 "start the instance again. Without it the game exits at launch " &
                                 "with a 'no suitable graphics device' error."
            }},
            {"linux-unzip", New CatalogEntry With {
                .DisplayName = "unzip (Linux)",
                .DownloadUrl = "",
                .Instructions = "Some plugin operations extract .zip archives with the system " &
                                 "unzip tool (python3 is used as a fallback when present). " &
                                 "Install it on the node with: sudo apt install unzip."
            }}
        }

        ''' <summary>
        ''' Check a list of prereq names. Returns one result per name
        ''' in the same order. Names not in the catalog return
        ''' Recognized=False; the Manager silently skips those.
        ''' Whitespace-only and Nothing entries are skipped without
        ''' producing a result (so the Manager doesn't render a
        ''' notice for a typo).
        ''' </summary>
        Public Function Check(names As IReadOnlyList(Of String)) As PrerequisiteCheckResponse
            Dim resp As New PrerequisiteCheckResponse With {
                .Results = New List(Of PrerequisiteCheckResult)()
            }
            If names Is Nothing Then Return resp
            For Each name In names
                If String.IsNullOrWhiteSpace(name) Then Continue For
                resp.Results.Add(CheckSingle(name.Trim()))
            Next
            Return resp
        End Function

        ''' <summary>
        ''' Resolve a single name to a result. Looks up the catalog
        ''' entry for display fields, then dispatches to the
        ''' appropriate detection helper based on the name. Names
        ''' not in the catalog return a Recognized=False stub.
        ''' </summary>
        Private Function CheckSingle(name As String) As PrerequisiteCheckResult
            Dim result As New PrerequisiteCheckResult With {
                .Name = name,
                .Recognized = False,
                .Installed = False,
                .Version = ""
            }

            Dim entry As CatalogEntry = Nothing
            If Not _catalog.TryGetValue(name, entry) Then Return result

            result.Recognized = True
            result.DisplayName = entry.DisplayName
            result.DownloadUrl = entry.DownloadUrl
            result.Instructions = entry.Instructions

            ' Dispatch on the canonical lowercased name. New catalog
            ' entries need a matching case here pointing at their
            ' detection helper.
            Select Case name.ToLowerInvariant()
                Case "vcredist-2015-2022-x64"
                    Dim probe = ProbeVcRedistX64()
                    result.Installed = probe.Installed
                    result.Version = probe.Version
                Case "linux-xvfb"
                    Dim probe = ProbeLinuxBinary("xvfb-run")
                    result.Installed = probe.Installed
                    result.Version = probe.Version
                Case "linux-unzip"
                    ' python3's zipfile module is an accepted fallback
                    ' for zip extraction, so either binary satisfies
                    ' the prereq.
                    Dim probe = ProbeLinuxBinary("unzip")
                    If Not probe.Installed Then probe = ProbeLinuxBinary("python3")
                    result.Installed = probe.Installed
                    result.Version = probe.Version
            End Select

            Return result
        End Function

        ''' <summary>
        ''' Output of a per-prereq probe helper. Two-tuple shape so
        ''' helpers can stay structurally identical across catalog
        ''' entries; adding fields here (e.g. install path, install
        ''' date) doesn't require changing every helper signature.
        ''' </summary>
        Private Structure ProbeResult
            Public Installed As Boolean
            Public Version As String
        End Structure

        ''' <summary>
        ''' Probe for Microsoft VC++ 2015-2022 Redistributable (x64).
        '''
        ''' Detection: read
        '''   HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64
        ''' under the 64-bit registry view. This is the canonical
        ''' "is it installed?" key that Microsoft's own installer
        ''' writes and that the Visual Studio installer checks. The
        ''' 14.x ABI is shared by VC++ 2015, 2017, 2019, and 2022 —
        ''' installing any of them populates the same key, so the
        ''' single probe covers all four marketing names.
        '''
        ''' "Installed" is a DWORD; 1 means present. We also read
        ''' "Version" (formatted like "14.38.33135.0") for
        ''' diagnostics; the notice fires off Installed alone but
        ''' the version round-trips through the response in case a
        ''' future iteration wants to show "you have X, latest is Y".
        '''
        ''' Always returns (false, "") on non-Windows so a Linux
        ''' node querying this prereq doesn't throw. The CA1416
        ''' analyzer would flag the registry calls below if the
        ''' OperatingSystem.IsWindows() check weren't here.
        '''
        ''' Registry permission failures are swallowed and treated
        ''' as "not installed" — false positives on the missing
        ''' side are vastly preferable to false negatives (which
        ''' would let the user proceed into a silent-crash install).
        ''' </summary>
        Private Function ProbeVcRedistX64() As ProbeResult
            Dim result As ProbeResult
            result.Installed = False
            result.Version = ""

            If Not OperatingSystem.IsWindows() Then Return result

            Try
                Using hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                                                      RegistryView.Registry64)
                    Using key = hklm.OpenSubKey(
                        "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
                        writable:=False)
                        If key Is Nothing Then Return result

                        Dim installedVal = key.GetValue("Installed", 0)
                        Dim installedInt As Integer = 0
                        If installedVal IsNot Nothing Then
                            Integer.TryParse(installedVal.ToString(), installedInt)
                        End If
                        result.Installed = (installedInt = 1)

                        Dim versionVal = TryCast(key.GetValue("Version"), String)
                        If Not String.IsNullOrEmpty(versionVal) Then
                            result.Version = versionVal
                        End If
                    End Using
                End Using
            Catch
                ' Registry permission / missing-hive / corrupt-key
                ' failures fall through to (false, "") — see method
                ' header for rationale.
            End Try

            Return result
        End Function

        ''' <summary>
        ''' Probe for a Linux command-line binary by walking PATH —
        ''' the managed equivalent of `command -v`, without spawning
        ''' a shell. On NON-Linux nodes returns Installed=True
        ''' ("satisfied / not applicable") — the plugin contract's
        ''' GetRequiredPrerequisites takes no platform parameter, so
        ''' plugins declare Linux prereqs unconditionally and the
        ''' probe must not fire false missing-notices on Windows.
        ''' Version detection is skipped — presence is the only
        ''' question the notices ask.
        ''' </summary>
        Private Function ProbeLinuxBinary(binaryName As String) As ProbeResult
            Dim result As ProbeResult
            result.Installed = False
            result.Version = ""

            If Not OperatingSystem.IsLinux() Then
                result.Installed = True
                Return result
            End If

            Try
                Dim pathVar = Environment.GetEnvironmentVariable("PATH")
                If String.IsNullOrEmpty(pathVar) Then Return result
                For Each dirPath In pathVar.Split(":"c)
                    If String.IsNullOrWhiteSpace(dirPath) Then Continue For
                    If IO.File.Exists(IO.Path.Combine(dirPath, binaryName)) Then
                        result.Installed = True
                        Exit For
                    End If
                Next
            Catch
                ' PATH parse / IO failures fall through to "not
                ' installed" — same missing-side bias as the registry
                ' probe above.
            End Try

            Return result
        End Function

    End Class

End Namespace
