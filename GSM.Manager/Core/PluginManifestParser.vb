Imports System
Imports System.Collections.Generic
Imports System.Text.RegularExpressions
Imports GSM.Utility

' ============================================================
'  PluginManifestParser — Phase 6-1
'
'  Parses a plugin's inline manifest from the comment header of
'  its .vb source — no sidecar JSON. Extends the existing
'  ' <RequiresContracts: N> magic-comment convention with a
'  richer ' <plugin ...> attribute line and an optional
'  ' <dependencies> ... ' </dependencies> block.
'
'  Format (all inside VB comments at the top of the file):
'
'    ' <plugin id="factorio" name="Factorio Headless Server" version="1.2.0" author="powergsm" requiresContracts="1">
'    ' <dependencies>
'    '   <depends id="some_shared_lib" min="0.4.0" />
'    ' </dependencies>
'
'  Keep <plugin ...> on a single comment line; attribute values
'  must not contain '>'. The <dependencies> block may span lines.
'
'  Back-compat: a file carrying only the legacy
'  ' <RequiresContracts: N> comment (and no <plugin> block) still
'  parses — HasPluginBlock is False, RequiresContracts is filled
'  from the legacy comment, and PluginRegistry treats it as an
'  untracked local plugin. The legacy comment is phased out
'  slowly, not removed now.
'
'  Parsing is regex/line-based and whitespace-tolerant, the same
'  spirit as PluginRegistry's RequiresContracts regex. It runs
'  once per plugin source per reload (a handful of files), so the
'  cost is negligible.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>One declared plugin dependency (id + minimum semver).</summary>
    Public NotInheritable Class PluginDependency
        Public Property Id As String
        ''' <summary>Raw minimum-version string (semver), or Nothing.</summary>
        Public Property Min As String
    End Class

    ''' <summary>
    ''' Parsed inline plugin manifest. Every field is optional — a
    ''' fully-described first-party plugin fills them all; a legacy or
    ''' manifest-less drop-in may fill none (HasPluginBlock False).
    ''' </summary>
    Public NotInheritable Class PluginManifest
        Public Property Id As String
        Public Property Name As String
        ''' <summary>Raw version string (semver), or Nothing when absent
        ''' — absence means the plugin isn't update-tracked.</summary>
        Public Property Version As String
        Public Property Author As String
        Public Property Description As String
        ''' <summary>Declared contracts version, from the &lt;plugin&gt;
        ''' attribute or the legacy comment; Nothing when neither is
        ''' present.</summary>
        Public Property RequiresContracts As Integer?
        Public Property Dependencies As New List(Of PluginDependency)

        ''' <summary>Phase 7-3 — declared capabilities from the
        ''' `requires` attribute (normalised lower-case list; empty
        ''' when absent). Informational consent + context gating for
        ''' utility plugins; game plugins may omit it entirely.</summary>
        Public Property Requires As New List(Of String)

        ''' <summary>True when a real &lt;plugin ...&gt; block was found
        ''' (vs a legacy-only or manifest-less file).</summary>
        Public Property HasPluginBlock As Boolean
    End Class

    Public NotInheritable Class PluginManifestParser

        ' <plugin ...> — require a leading comment tick so the tag is
        ' only ever matched inside a comment, never a string literal.
        ' Group 1 is the raw attribute soup up to the closing '>'.
        Private Shared ReadOnly s_PluginTag As New Regex(
            "'\s*<plugin\b([^>]*)>",
            RegexOptions.IgnoreCase Or RegexOptions.Compiled)

        ' <dependencies> ... </dependencies> — Singleline so the inner
        ' capture spans the comment lines between the open/close tags.
        Private Shared ReadOnly s_DependenciesBlock As New Regex(
            "<dependencies\s*>(.*?)</dependencies\s*>",
            RegexOptions.IgnoreCase Or RegexOptions.Singleline Or RegexOptions.Compiled)

        ' <depends ... /> — one per dependency, self-closing or not.
        Private Shared ReadOnly s_DependsTag As New Regex(
            "<depends\b([^>]*?)/?\s*>",
            RegexOptions.IgnoreCase Or RegexOptions.Compiled)

        ' key="value" attribute pairs.
        Private Shared ReadOnly s_Attr As New Regex(
            "(\w+)\s*=\s*""([^""]*)""",
            RegexOptions.Compiled)

        ' Legacy ' <RequiresContracts: N> — same pattern PluginRegistry
        ' uses, kept here so the manifest can carry the value too.
        Private Shared ReadOnly s_LegacyRequiresContracts As New Regex(
            "'\s*<RequiresContracts\s*:\s*(\d+)\s*>",
            RegexOptions.Compiled)

        ''' <summary>
        ''' Parse a plugin manifest from a .vb source's text. Never
        ''' throws; an absent or malformed manifest yields an all-empty
        ''' manifest (HasPluginBlock False) rather than an error, so a
        ''' manifest-less file still loads as a local plugin.
        ''' </summary>
        Public Shared Function Parse(sourceText As String) As PluginManifest
            Dim m As New PluginManifest()
            If String.IsNullOrEmpty(sourceText) Then Return m

            Try
                Dim pluginMatch = s_PluginTag.Match(sourceText)
                If pluginMatch.Success Then
                    m.HasPluginBlock = True
                    Dim attrs = ParseAttributes(pluginMatch.Groups(1).Value)
                    m.Id = GetOrNothing(attrs, "id")
                    m.Name = GetOrNothing(attrs, "name")
                    m.Version = GetOrNothing(attrs, "version")
                    m.Author = GetOrNothing(attrs, "author")
                    m.Description = GetOrNothing(attrs, "description")
                    Dim rcText = GetOrNothing(attrs, "requiresContracts")
                    Dim rcVal As Integer
                    If Not String.IsNullOrEmpty(rcText) AndAlso Integer.TryParse(rcText, rcVal) Then
                        m.RequiresContracts = rcVal
                    End If

                    ' Phase 7-3 — declared capabilities.
                    m.Requires = UtilityCapabilities.ParseList(GetOrNothing(attrs, "requires"))
                End If

                Dim depBlock = s_DependenciesBlock.Match(sourceText)
                If depBlock.Success Then
                    For Each dm As Match In s_DependsTag.Matches(depBlock.Groups(1).Value)
                        Dim depAttrs = ParseAttributes(dm.Groups(1).Value)
                        Dim depId = GetOrNothing(depAttrs, "id")
                        If Not String.IsNullOrEmpty(depId) Then
                            m.Dependencies.Add(New PluginDependency With {
                                .Id = depId,
                                .Min = GetOrNothing(depAttrs, "min")
                            })
                        End If
                    Next
                End If

                ' Legacy fallback for the contracts version only.
                If Not m.RequiresContracts.HasValue Then
                    Dim legacy = s_LegacyRequiresContracts.Match(sourceText)
                    If legacy.Success Then
                        Dim v As Integer
                        If Integer.TryParse(legacy.Groups(1).Value, v) Then
                            m.RequiresContracts = v
                        End If
                    End If
                End If
            Catch
                ' Defensive: never let a malformed header break loading —
                ' return whatever we managed to populate.
            End Try

            Return m
        End Function

        Private Shared Function ParseAttributes(attrText As String) As Dictionary(Of String, String)
            Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If String.IsNullOrEmpty(attrText) Then Return d
            For Each am As Match In s_Attr.Matches(attrText)
                d(am.Groups(1).Value) = am.Groups(2).Value
            Next
            Return d
        End Function

        Private Shared Function GetOrNothing(d As Dictionary(Of String, String), key As String) As String
            Dim v As String = Nothing
            If d.TryGetValue(key, v) AndAlso Not String.IsNullOrEmpty(v) Then Return v
            Return Nothing
        End Function

    End Class

End Namespace
