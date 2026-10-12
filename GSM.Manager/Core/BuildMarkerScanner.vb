Imports System
Imports System.IO
Imports System.Linq
Imports System.Text
Imports GSM.Plugin

' ============================================================
'  Build-marker scanner (Phase 8-2 follow-up 5a)
'
'  Every PowerGSM binary from 0.8 on carries an MSBuild-generated
'  string constant of the shape
'
'      PGSMBUILD|<component>|<version>|<rid>|
'
'  (see Directory.Build.targets at the solution root). .NET string
'  constants land verbatim in assembly metadata — UTF-16LE, and
'  uncompressed even inside self-contained single-file bundles —
'  so a picked file's component/version/platform can be read from
'  its BYTES without executing it. That is the fix for the July
'  2026 incident's failure mode: a Linux ELF on a Windows disk is
'  version-anonymous and cannot be executed to ask.
'
'  The scanner checks UTF-16LE first (where .NET metadata puts it)
'  and falls back to UTF-8 for robustness against future packers /
'  build shapes. The trailing '|' in the marker is the parse
'  terminator — metadata packs the next heap entry directly behind
'  the constant, so the <rid> field needs a definite end.
'
'  False-positive hardening: any occurrence of the prefix whose
'  payload doesn't parse as three well-formed fields is skipped and
'  the scan continues (the Manager's own binary, for instance,
'  contains the bare prefix right here in this file's constant).
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>
    ''' The parsed identity of an embedded PGSMBUILD marker: which
    ''' component the file is, what version it was built as, and the
    ''' RuntimeIdentifier it was built for.
    ''' </summary>
    Public NotInheritable Class BuildMarkerInfo
        Public Property Component As String = ""
        Public Property Version As String = ""
        Public Property Rid As String = ""

        ''' <summary>OS the marker's RID targets, mapped onto the same
        ''' NodePlatform axis the nodes report.</summary>
        Public ReadOnly Property Platform As NodePlatform
            Get
                If Rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) Then Return NodePlatform.Windows
                If Rid.StartsWith("linux", StringComparison.OrdinalIgnoreCase) Then Return NodePlatform.Linux
                Return NodePlatform.Unknown
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return $"{Component} {Version} ({Rid})"
        End Function
    End Class

    ''' <summary>
    ''' Scans a file's raw bytes for the embedded PGSMBUILD build marker.
    ''' </summary>
    Public Module BuildMarkerScanner

        Private Const MarkerPrefix As String = "PGSMBUILD|"

        ' Payload budget after the prefix: three fields + pipes is well
        ' under this; a bigger window just tolerates junk before parse
        ' validation rejects it.
        Private Const MaxPayloadChars As Integer = 128

        ' Refuse to slurp absurd files (the shipped binaries are tens of
        ' MB; anything beyond this is not a PowerGSM component).
        Private Const MaxScanBytes As Long = 512L * 1024 * 1024

        ''' <summary>
        ''' Read a file and return its parsed build marker, or Nothing when
        ''' no valid marker is present (pre-0.8 build, foreign file,
        ''' unreadable path — the caller can't distinguish these, which is
        ''' the point: an unidentifiable binary is unidentifiable).
        ''' Never throws.
        ''' </summary>
        Public Function TryRead(path As String) As BuildMarkerInfo
            Try
                Dim fi As New FileInfo(path)
                If Not fi.Exists OrElse fi.Length = 0 OrElse fi.Length > MaxScanBytes Then Return Nothing
                Dim bytes = File.ReadAllBytes(path)
                ' UTF-16LE first: that's where .NET assembly metadata puts
                ' string constants, so it's the hit for every real binary.
                Dim hit = Scan(bytes, Encoding.Unicode)
                If hit Is Nothing Then hit = Scan(bytes, Encoding.UTF8)
                Return hit
            Catch
                Return Nothing
            End Try
        End Function

        Private Function Scan(bytes As Byte(), enc As Encoding) As BuildMarkerInfo
            Dim needle = enc.GetBytes(MarkerPrefix)
            Dim bytesPerChar = If(enc Is Encoding.Unicode, 2, 1)
            Dim start = 0
            Do
                Dim idx = IndexOf(bytes, needle, start)
                If idx < 0 Then Return Nothing
                Dim payloadStart = idx + needle.Length
                Dim take = Math.Min(MaxPayloadChars * bytesPerChar, bytes.Length - payloadStart)
                If take > 0 Then
                    Dim payload As String = ""
                    Try
                        payload = enc.GetString(bytes, payloadStart, take)
                    Catch
                    End Try
                    Dim parsed = Parse(payload)
                    If parsed IsNot Nothing Then Return parsed
                End If
                ' Not a valid marker (e.g. the bare prefix constant in the
                ' Manager's own scanner) — keep looking.
                start = idx + 1
            Loop
        End Function

        ''' <summary>
        ''' Parse "&lt;component&gt;|&lt;version&gt;|&lt;rid&gt;|…" — the three fields up
        ''' to the terminating third pipe, with charset validation so heap
        ''' junk behind a stray prefix never masquerades as a marker.
        ''' </summary>
        Private Function Parse(payload As String) As BuildMarkerInfo
            Dim p1 = payload.IndexOf("|"c)
            If p1 <= 0 Then Return Nothing
            Dim p2 = payload.IndexOf("|"c, p1 + 1)
            If p2 <= p1 + 1 Then Return Nothing
            Dim p3 = payload.IndexOf("|"c, p2 + 1)
            If p3 <= p2 + 1 Then Return Nothing

            Dim comp = payload.Substring(0, p1)
            Dim ver = payload.Substring(p1 + 1, p2 - p1 - 1)
            Dim rid = payload.Substring(p2 + 1, p3 - p2 - 1)

            If Not FieldOk(comp) OrElse Not FieldOk(ver) OrElse Not FieldOk(rid) Then Return Nothing
            ' Every PowerGSM component is GSM.*; a version carries at least
            ' one digit. Cheap sanity that kills essentially all noise.
            If Not comp.StartsWith("GSM.", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            If Not ver.Any(AddressOf Char.IsDigit) Then Return Nothing

            Return New BuildMarkerInfo With {.Component = comp, .Version = ver, .Rid = rid}
        End Function

        Private Function FieldOk(s As String) As Boolean
            If String.IsNullOrEmpty(s) OrElse s.Length > 64 Then Return False
            For Each c As Char In s
                Dim ok = Char.IsLetterOrDigit(c) OrElse c = "."c OrElse c = "-"c OrElse c = "_"c OrElse c = "+"c
                If Not ok Then Return False
            Next
            Return True
        End Function

        ''' <summary>Naive byte-pattern search; the binaries are tens of MB
        ''' and this runs once per file pick, so simple beats clever.</summary>
        Private Function IndexOf(haystack As Byte(), needle As Byte(), start As Integer) As Integer
            If needle.Length = 0 OrElse haystack.Length < needle.Length Then Return -1
            Dim lastStart = haystack.Length - needle.Length
            For i = Math.Max(0, start) To lastStart
                If haystack(i) = needle(0) Then
                    Dim match = True
                    For j = 1 To needle.Length - 1
                        If haystack(i + j) <> needle(j) Then
                            match = False
                            Exit For
                        End If
                    Next
                    If match Then Return i
                End If
            Next
            Return -1
        End Function

    End Module

End Namespace
