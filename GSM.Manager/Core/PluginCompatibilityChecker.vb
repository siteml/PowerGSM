Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports Basic.Reference.Assemblies
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.Extensions.Logging
Imports GSM.Plugin

' ============================================================
'  PluginCompatibilityChecker — Phase 5l-3
'
'  Dry-run Roslyn compile of every plugin .vb against a chosen
'  GSM.Contracts.dll, to answer "will these plugins still load if
'  the Manager's contracts assembly changes?" — authoritatively,
'  by actually compiling, rather than guessing from version markers.
'
'  Two callers:
'    • Apply pre-flight (5l-3): compile against the STAGED
'      Contracts.dll before swapping binaries.
'    • Tools > Test plugin compatibility: compile against the
'      CURRENTLY-LOADED Contracts.dll (contractsDllPath = Nothing).
'
'  The compile mirrors PluginRegistry exactly (same references,
'  same options, same Emit) so a "compatible" verdict here means
'  the file would load cleanly there.
' ============================================================

Namespace GSM.Manager.Core

    Public Class PluginCompatibilityError
        Public Property ErrorCode As String
        Public Property Line As Integer
        Public Property Message As String
    End Class

    Public Class PluginCompatibilityResult
        Public Property FileName As String
        Public Property Compatible As Boolean
        Public Property Errors As New List(Of PluginCompatibilityError)
    End Class

    Public Class PluginCompatibilityReport
        ''' <summary>Human label for what was compiled against, e.g. "v0.4.0 (staged)".</summary>
        Public Property ContractsLabel As String
        Public Property Results As New List(Of PluginCompatibilityResult)

        Public ReadOnly Property PluginCount As Integer
            Get
                Return Results.Count
            End Get
        End Property

        Public ReadOnly Property AnyIncompatible As Boolean
            Get
                Return Results.Any(Function(r) Not r.Compatible)
            End Get
        End Property

        Public ReadOnly Property IncompatibleCount As Integer
            Get
                Return Results.Where(Function(r) Not r.Compatible).Count()
            End Get
        End Property
    End Class

    Public Class PluginCompatibilityChecker

        Private ReadOnly _logger As ILogger(Of PluginCompatibilityChecker)
        Private ReadOnly _registry As PluginRegistry

        Public Sub New(logger As ILogger(Of PluginCompatibilityChecker),
                       registry As PluginRegistry)
            _logger = logger
            _registry = registry
        End Sub

        ''' <summary>
        ''' Compile every plugin source against the contracts assembly at
        ''' <paramref name="contractsDllPath"/> (or, when Nothing/empty,
        ''' the currently-loaded GSM.Contracts). Never throws — a failure
        ''' to locate references surfaces as every plugin incompatible
        ''' with an explanatory error.
        ''' </summary>
        Public Function Check(contractsDllPath As String, contractsLabel As String) As PluginCompatibilityReport
            Dim report As New PluginCompatibilityReport With {.ContractsLabel = contractsLabel}

            Dim dir = _registry.PluginsDirectory
            If String.IsNullOrEmpty(dir) OrElse Not Directory.Exists(dir) Then Return report

            Dim files = Directory.GetFiles(dir, "*.vb", SearchOption.TopDirectoryOnly)
            If files.Length = 0 Then Return report

            Dim references As List(Of MetadataReference)
            Try
                references = BuildReferences(contractsDllPath)
            Catch ex As Exception
                ' No usable contracts reference — report all plugins as
                ' not-checkable rather than throwing into the UI.
                _logger.LogWarning(ex, "PluginCompatibilityChecker: could not build references")
                For Each f In files
                    report.Results.Add(New PluginCompatibilityResult With {
                        .FileName = Path.GetFileName(f),
                        .Compatible = False,
                        .Errors = New List(Of PluginCompatibilityError) From {
                            New PluginCompatibilityError With {.Message = "Couldn't prepare compiler references: " & ex.Message}
                        }
                    })
                Next
                Return report
            End Try

            Dim options As New VisualBasicCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optionStrict:=OptionStrict.Off,
                optionExplicit:=True,
                optionInfer:=True)

            For Each filePath In files
                report.Results.Add(CheckFile(filePath, references, options))
            Next

            Return report
        End Function

        Private Function CheckFile(filePath As String,
                                   references As List(Of MetadataReference),
                                   options As VisualBasicCompilationOptions) As PluginCompatibilityResult
            Dim fileName = Path.GetFileName(filePath)
            Dim res As New PluginCompatibilityResult With {.FileName = fileName, .Compatible = True}

            Try
                Dim source = File.ReadAllText(filePath)
                Dim tree = VisualBasicSyntaxTree.ParseText(source, path:=filePath)
                Dim compilation = VisualBasicCompilation.Create(
                    "GSM.PluginCompat." & Path.GetFileNameWithoutExtension(filePath),
                    {tree}, references, options)

                ' Emit (not just GetDiagnostics) to match PluginRegistry's
                ' real load path exactly — same diagnostics it would see.
                Using ms As New MemoryStream()
                    Dim emit = compilation.Emit(ms)
                    If Not emit.Success Then
                        res.Compatible = False
                        For Each d In emit.Diagnostics.
                                Where(Function(x) x.Severity = DiagnosticSeverity.Error)
                            Dim ls = d.Location.GetLineSpan()
                            res.Errors.Add(New PluginCompatibilityError With {
                                .ErrorCode = d.Id,
                                .Line = ls.StartLinePosition.Line + 1,
                                .Message = d.GetMessage()
                            })
                        Next
                    End If
                End Using
            Catch ex As Exception
                res.Compatible = False
                res.Errors.Add(New PluginCompatibilityError With {.Message = "Check failed: " & ex.Message})
            End Try

            Return res
        End Function

        ''' <summary>
        ''' .NET 8 BCL refs (Basic.Reference.Assemblies, deployment-shape
        ''' independent) + the chosen GSM.Contracts.dll. Mirrors
        ''' PluginRegistry.GetMetadataReferences but with a swappable
        ''' contracts path.
        ''' </summary>
        Private Shared Function BuildReferences(contractsDllPath As String) As List(Of MetadataReference)
            Dim refs As New List(Of MetadataReference)
            refs.AddRange(ReferenceAssemblies.Net80)

            Dim contractsRef As MetadataReference = Nothing
            If Not String.IsNullOrEmpty(contractsDllPath) AndAlso File.Exists(contractsDllPath) Then
                contractsRef = MetadataReference.CreateFromFile(contractsDllPath)
            Else
                ' Currently-loaded contracts: the assembly defining IGamePlugin.
                Dim loaded = GetType(IGamePlugin).Assembly.Location
                If Not String.IsNullOrEmpty(loaded) AndAlso File.Exists(loaded) Then
                    contractsRef = MetadataReference.CreateFromFile(loaded)
                Else
                    Dim side = Path.Combine(AppContext.BaseDirectory, "GSM.Contracts.dll")
                    If File.Exists(side) Then contractsRef = MetadataReference.CreateFromFile(side)
                End If
            End If

            If contractsRef Is Nothing Then
                Throw New InvalidOperationException(
                    "Could not locate a GSM.Contracts.dll to compile plugins against.")
            End If
            refs.Add(contractsRef)
            Return refs
        End Function

    End Class

End Namespace
