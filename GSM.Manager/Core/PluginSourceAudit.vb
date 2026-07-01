Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax
Imports GSM.Utility

' ============================================================
'  PluginSourceAudit — Phase 7-3b (static ratchet, part 2)
'
'  A read-only syntax-tree scan of plugin source for the
'  capability-relevant operations the reference-set gate can't
'  catch: P/Invoke (DllImport), process launching (Process.Start),
'  and reflection (Type.GetType / Assembly.Load / Activator).
'
'  This is NOT a sandbox and not a hard block. Plugins are
'  full-trust compiled code; a determined author can obfuscate
'  past any static scan (reflection-by-string especially). The
'  value is honesty: surface "this plugin does X" in the INSTALL
'  CONSENT so the operator approves with eyes open, and nudge
'  honest authors toward declaring matching capabilities. The
'  real defenses remain provenance + readable source + never-auto.
'
'  Findings are advisory strings shown alongside the staging
'  warnings. Where a finding maps to a declarable capability the
'  plugin DIDN'T declare, that gap is called out — but we never
'  refuse the install over it.
'
'  Detection is deliberately syntactic and conservative:
'  identifier-name matching on invocation/attribute syntax. False
'  positives (e.g. an unrelated user method named "Start") are
'  acceptable for an advisory; false negatives are expected for
'  obfuscated code and are explicitly out of scope (documented in
'  Phase7_Plan.md alongside the no-out-of-process-host decision).
' ============================================================

Namespace GSM.Manager.Core

    Public NotInheritable Class PluginAuditFinding
        Public Property Title As String
        ''' <summary>Capability that would normally cover this, if any
        ''' (Nothing when the operation has no declarable capability —
        ''' DllImport/reflection are inherently undeclarable in v1).</summary>
        Public Property RelatedCapability As String
        Public Property Detail As String
    End Class

    Public NotInheritable Class PluginSourceAudit

        ''' <summary>
        ''' Scan source text for capability-relevant operations.
        ''' Returns an empty list for clean source or unparseable
        ''' input (parse failures surface elsewhere as compile errors;
        ''' the audit just stays quiet). declaredCapabilities lets the
        ''' findings note when a detected operation wasn't declared.
        ''' </summary>
        Public Shared Function Scan(sourceText As String,
                                    declaredCapabilities As IEnumerable(Of String)) As List(Of PluginAuditFinding)
            Dim findings As New List(Of PluginAuditFinding)
            If String.IsNullOrWhiteSpace(sourceText) Then Return findings

            Dim declared = New HashSet(Of String)(
                If(declaredCapabilities, Enumerable.Empty(Of String)()),
                StringComparer.OrdinalIgnoreCase)

            Dim tree As SyntaxTree
            Try
                tree = VisualBasicSyntaxTree.ParseText(sourceText)
            Catch
                Return findings
            End Try

            Dim root = tree.GetRoot()

            ' --- DllImport attribute (P/Invoke) ---
            Dim hasDllImport = root.DescendantNodes().OfType(Of AttributeSyntax)().
                Any(Function(a) AttributeNameContains(a, "DllImport"))
            If hasDllImport Then
                findings.Add(New PluginAuditFinding With {
                    .Title = "Calls native code (P/Invoke / DllImport)",
                    .RelatedCapability = Nothing,
                    .Detail = "This plugin declares a native function import. Native calls run outside anything PowerGSM can mediate — review the source before trusting it."})
            End If

            ' --- Process.Start (launching processes) ---
            Dim startsProcess = root.DescendantNodes().OfType(Of MemberAccessExpressionSyntax)().
                Any(Function(ma) IdentifierIs(ma.Name, "Start") AndAlso ExpressionMentions(ma.Expression, "Process"))
            If startsProcess Then
                findings.Add(New PluginAuditFinding With {
                    .Title = "Launches external processes (Process.Start)",
                    .RelatedCapability = Nothing,
                    .Detail = "This plugin starts other programs. Review which executables it runs."})
            End If

            ' --- Reflection (Type.GetType / Assembly.Load / Activator) ---
            Dim usesReflection = root.DescendantNodes().OfType(Of MemberAccessExpressionSyntax)().
                Any(Function(ma)
                        Return (IdentifierIs(ma.Name, "GetType") AndAlso ExpressionMentions(ma.Expression, "Type")) OrElse
                               (IdentifierIs(ma.Name, "Load") AndAlso ExpressionMentions(ma.Expression, "Assembly")) OrElse
                               ExpressionMentions(ma.Expression, "Activator")
                    End Function)
            If usesReflection Then
                findings.Add(New PluginAuditFinding With {
                    .Title = "Uses reflection (dynamic type / assembly loading)",
                    .RelatedCapability = Nothing,
                    .Detail = "Reflection can reach code that bypasses the capability model. Treat this plugin's source with extra scrutiny."})
            End If

            ' --- Network use without the declared capability ---
            ' The reference-set gate (PluginRegistry) makes undeclared
            ' network a COMPILE error, so this is a courtesy heads-up at
            ' stage time before that bite: mention it only when the
            ' source clearly references networking yet didn't declare it.
            If Not declared.Contains(UtilityCapabilities.Network) Then
                Dim mentionsNet = root.DescendantNodes().OfType(Of IdentifierNameSyntax)().
                    Any(Function(idn) idn.Identifier.ValueText = "HttpClient" OrElse
                                      idn.Identifier.ValueText = "WebClient" OrElse
                                      idn.Identifier.ValueText = "TcpClient" OrElse
                                      idn.Identifier.ValueText = "Socket")
                If mentionsNet Then
                    findings.Add(New PluginAuditFinding With {
                        .Title = "Appears to use the network but didn't declare it",
                        .RelatedCapability = UtilityCapabilities.Network,
                        .Detail = "Network types are referenced without requires=""network"". This plugin will fail to compile until it declares the network capability."})
                End If
            End If

            Return findings
        End Function

        ''' <summary>Render findings as the lines shown under a plugin in
        ''' the install/update consent (empty list = no lines).</summary>
        Public Shared Function ToConsentLines(findings As IEnumerable(Of PluginAuditFinding)) As List(Of String)
            Dim lines As New List(Of String)
            If findings Is Nothing Then Return lines
            For Each f In findings
                lines.Add(f.Title)
            Next
            Return lines
        End Function

        ' --- syntax helpers ---

        Private Shared Function AttributeNameContains(attr As AttributeSyntax, name As String) As Boolean
            Return attr.Name.ToString().
                EndsWith(name, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function IdentifierIs(nameNode As SimpleNameSyntax, name As String) As Boolean
            Return nameNode IsNot Nothing AndAlso
                   String.Equals(nameNode.Identifier.ValueText, name, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>True when an expression's text mentions a bare
        ''' identifier (e.g. the "Process" in "Process.Start" or
        ''' "Diagnostics.Process"). Deliberately loose — advisory only.</summary>
        Private Shared Function ExpressionMentions(expr As ExpressionSyntax, identifier As String) As Boolean
            If expr Is Nothing Then Return False
            Return expr.ToString().
                Split(New Char() {"."c, " "c, "("c}, StringSplitOptions.RemoveEmptyEntries).
                Any(Function(part) String.Equals(part, identifier, StringComparison.OrdinalIgnoreCase))
        End Function

    End Class

End Namespace
