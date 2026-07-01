Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data

' ============================================================
'  PluginCatalogService — Phase 6-2
'
'  Reads plugin sources (GitHub repos) and turns each into a
'  catalog of available plugins by:
'    1. listing .vb files in the source's RepoPath via the
'       GitHub contents API (one rate-limited call per source), then
'    2. fetching each file's text from raw.githubusercontent.com
'       (no rate limit) and parsing its inline <plugin> manifest
'       with PluginManifestParser.
'
'  Also owns plugin-source persistence (CRUD over PluginSourceEntity)
'  and the first-run seed of the official source. The official
'  source is privileged: its plugins may use bare ids; third-party
'  sources are expected to use an {owner}_{id} form, validated at
'  stage/install time in 6-3 (a bare/colliding id from a non-
'  official source is a warn-and-confirm, never a silent shadow).
'
'  Mirrors GitHubReleaseChecker's HttpClient discipline (User-Agent,
'  github+json accept, api-version header) and never throws to the
'  caller — fetches return a CatalogResult carrying either entries
'  or an error string.
'
'  GitHub rate limit: unauthenticated contents API is 60 req/hr/IP.
'  One list call per source per browse, with a per-session cache, is
'  well within budget; the raw header fetches don't count against it.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>Origin classification for a catalogued plugin.</summary>
    Public Enum PluginOrigin
        Official
        ThirdParty
    End Enum

    ''' <summary>One plugin available from a source's catalog.</summary>
    Public NotInheritable Class CatalogEntry
        Public Property Manifest As PluginManifest
        Public Property FileName As String
        ''' <summary>raw.githubusercontent.com URL of the .vb source.</summary>
        Public Property DownloadUrl As String
        Public Property Origin As PluginOrigin
        Public Property SourceId As String
        Public Property SourceDisplayName As String
        ''' <summary>Owner the source belongs to (drives the expected
        ''' third-party id prefix).</summary>
        Public Property SourceOwner As String
    End Class

    ''' <summary>Result of fetching one source's catalog.</summary>
    Public NotInheritable Class CatalogResult
        Public Property Ok As Boolean
        Public Property ErrorMessage As String
        Public Property Entries As New List(Of CatalogEntry)
    End Class

    Public Class PluginCatalogService

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of PluginCatalogService)
        Private ReadOnly _http As HttpClient

        ' Per-session catalog cache, keyed by SourceId. Cleared by
        ' RefreshSource / when a source is edited. Keeps repeated
        ' browses of the same source off the rate-limited API.
        Private ReadOnly _cache As New Dictionary(Of String, CatalogResult)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _cacheLock As New Object()

        ' The seeded official source. Un-deletable; bare ids allowed.
        Public Const OfficialOwner As String = "siteml"
        Public Const OfficialRepo As String = "PowerGSM"
        Public Const OfficialRepoPath As String = "GSM.PluginsSource"
        Public Const OfficialBranch As String = "master"
        Public Const OfficialDisplayName As String = "PowerGSM (official)"

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of PluginCatalogService))
            _serviceProvider = serviceProvider
            _logger = logger

            _http = New HttpClient() With {.Timeout = TimeSpan.FromSeconds(30)}
            Try
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM")
            Catch
            End Try
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
        End Sub

        ' ============================================================
        '  Source persistence (CRUD) + first-run seed
        ' ============================================================

        ''' <summary>
        ''' Ensure the official source row exists. Idempotent — called
        ''' at startup after Migrate(). Matches on owner/repo/path so a
        ''' user can't end up with a duplicate official entry, and keeps
        ''' the IsOfficial flag set if a prior row drifted.
        ''' </summary>
        Public Sub EnsureOfficialSeeded()
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim existing = db.PluginSources.FirstOrDefault(
                        Function(s) s.Owner = OfficialOwner AndAlso
                                    s.Repo = OfficialRepo AndAlso
                                    s.RepoPath = OfficialRepoPath)
                    If existing Is Nothing Then
                        db.PluginSources.Add(New PluginSourceEntity With {
                            .SourceId = Guid.NewGuid().ToString("N"),
                            .DisplayName = OfficialDisplayName,
                            .Owner = OfficialOwner,
                            .Repo = OfficialRepo,
                            .RepoPath = OfficialRepoPath,
                            .Branch = OfficialBranch,
                            .IsOfficial = True,
                            .IsEnabled = True,
                            .LastFetchedUtc = Nothing
                        })
                        db.SaveChanges()
                        _logger.LogInformation("Seeded official plugin source {Owner}/{Repo}", OfficialOwner, OfficialRepo)
                    ElseIf Not existing.IsOfficial Then
                        existing.IsOfficial = True
                        db.SaveChanges()
                    End If
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "EnsureOfficialSeeded failed")
            End Try
        End Sub

        ''' <summary>All sources, official first then by display name.</summary>
        Public Function GetSources() As List(Of PluginSourceEntity)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Return db.PluginSources.
                        OrderByDescending(Function(s) s.IsOfficial).
                        ThenBy(Function(s) s.DisplayName).
                        ToList()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "GetSources failed")
                Return New List(Of PluginSourceEntity)()
            End Try
        End Function

        ''' <summary>
        ''' Add or update a source. A null/empty SourceId inserts;
        ''' otherwise updates the matching row. The official flag is
        ''' never settable from here — only EnsureOfficialSeeded sets
        ''' it — so a user can't mint a privileged source. Returns the
        ''' SourceId, or Nothing on failure.
        ''' </summary>
        Public Function SaveSource(source As PluginSourceEntity) As String
            If source Is Nothing Then Return Nothing
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row As PluginSourceEntity
                    If String.IsNullOrEmpty(source.SourceId) Then
                        row = New PluginSourceEntity With {.SourceId = Guid.NewGuid().ToString("N"), .IsOfficial = False}
                        db.PluginSources.Add(row)
                    Else
                        row = db.PluginSources.FirstOrDefault(Function(s) s.SourceId = source.SourceId)
                        If row Is Nothing Then Return Nothing
                    End If
                    row.DisplayName = source.DisplayName
                    row.Owner = source.Owner
                    row.Repo = source.Repo
                    row.RepoPath = If(source.RepoPath, "")
                    row.Branch = If(String.IsNullOrWhiteSpace(source.Branch), "master", source.Branch)
                    row.IsEnabled = source.IsEnabled
                    db.SaveChanges()
                    InvalidateCache(row.SourceId)
                    Return row.SourceId
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "SaveSource failed")
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Delete a source. The official source can't be deleted
        ''' (disable it instead) — returns False without touching it.
        ''' </summary>
        Public Function DeleteSource(sourceId As String) As Boolean
            If String.IsNullOrEmpty(sourceId) Then Return False
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.PluginSources.FirstOrDefault(Function(s) s.SourceId = sourceId)
                    If row Is Nothing Then Return False
                    If row.IsOfficial Then Return False
                    db.PluginSources.Remove(row)
                    db.SaveChanges()
                    InvalidateCache(sourceId)
                    Return True
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "DeleteSource failed")
                Return False
            End Try
        End Function

        Private Sub InvalidateCache(sourceId As String)
            SyncLock _cacheLock
                _cache.Remove(sourceId)
            End SyncLock
        End Sub

        ' ============================================================
        '  Catalog fetch
        ' ============================================================

        ''' <summary>
        ''' Fetch (or return cached) the catalog for one source. Lists
        ''' .vb files in the source's RepoPath, fetches each from raw,
        ''' and parses its manifest. Never throws.
        ''' </summary>
        Public Async Function GetCatalogAsync(source As PluginSourceEntity,
                                              Optional forceRefresh As Boolean = False,
                                              Optional ct As CancellationToken = Nothing) As Task(Of CatalogResult)
            If source Is Nothing Then
                Return New CatalogResult With {.Ok = False, .ErrorMessage = "No source."}
            End If

            If Not forceRefresh Then
                SyncLock _cacheLock
                    Dim cached As CatalogResult = Nothing
                    If _cache.TryGetValue(source.SourceId, cached) Then Return cached
                End SyncLock
            End If

            Dim result = Await FetchCatalogAsync(source, ct)

            If result.Ok Then
                SyncLock _cacheLock
                    _cache(source.SourceId) = result
                End SyncLock
                StampFetched(source.SourceId)
            End If
            Return result
        End Function

        Private Async Function FetchCatalogAsync(source As PluginSourceEntity,
                                                 ct As CancellationToken) As Task(Of CatalogResult)
            Dim result As New CatalogResult()
            Dim pathSegment = If(String.IsNullOrWhiteSpace(source.RepoPath), "", source.RepoPath.Trim().Trim("/"c))
            Dim branch = If(String.IsNullOrWhiteSpace(source.Branch), "master", source.Branch.Trim())

            ' 1) List the directory via the contents API.
            Dim listUrl = $"https://api.github.com/repos/{source.Owner}/{source.Repo}/contents/{pathSegment}?ref={branch}"
            Dim listJson As String
            Try
                Using resp = Await _http.GetAsync(listUrl, ct)
                    If Not resp.IsSuccessStatusCode Then
                        result.Ok = False
                        result.ErrorMessage = $"GitHub returned {CInt(resp.StatusCode)} listing {source.Owner}/{source.Repo}/{pathSegment}."
                        Return result
                    End If
                    listJson = Await resp.Content.ReadAsStringAsync(ct)
                End Using
            Catch ex As Exception
                result.Ok = False
                result.ErrorMessage = $"Couldn't reach GitHub: {ex.Message}"
                Return result
            End Try

            ' 2) Parse the listing; collect .vb files only.
            Dim vbFiles As New List(Of (Name As String, DownloadUrl As String))
            Try
                Using doc = JsonDocument.Parse(listJson)
                    If doc.RootElement.ValueKind <> JsonValueKind.Array Then
                        result.Ok = False
                        result.ErrorMessage = "Unexpected listing shape (is the path a folder?)."
                        Return result
                    End If
                    For Each item In doc.RootElement.EnumerateArray()
                        Dim itemType = GetStr(item, "type")
                        Dim name = GetStr(item, "name")
                        If String.Equals(itemType, "file", StringComparison.Ordinal) AndAlso
                           name IsNot Nothing AndAlso name.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) Then
                            vbFiles.Add((name, GetStr(item, "download_url")))
                        End If
                    Next
                End Using
            Catch ex As Exception
                result.Ok = False
                result.ErrorMessage = $"Couldn't parse GitHub listing: {ex.Message}"
                Return result
            End Try

            ' 3) Fetch + parse each file's manifest (raw — no rate limit).
            For Each f In vbFiles
                If ct.IsCancellationRequested Then Exit For
                Dim rawUrl = f.DownloadUrl
                If String.IsNullOrEmpty(rawUrl) Then
                    rawUrl = $"https://raw.githubusercontent.com/{source.Owner}/{source.Repo}/{branch}/{If(pathSegment = "", "", pathSegment & "/")}{f.Name}"
                End If

                Dim text As String = Nothing
                Try
                    text = Await _http.GetStringAsync(rawUrl, ct)
                Catch ex As Exception
                    _logger.LogDebug("Skipping {File}: fetch failed ({Msg})", f.Name, ex.Message)
                    Continue For
                End Try

                Dim manifest = PluginManifestParser.Parse(text)
                ' Only catalogue files that actually declare a <plugin>
                ' block — a repo may hold helper .vb files that aren't
                ' plugins. No block => not a catalogued plugin.
                If manifest Is Nothing OrElse Not manifest.HasPluginBlock Then Continue For

                result.Entries.Add(New CatalogEntry With {
                    .Manifest = manifest,
                    .FileName = f.Name,
                    .DownloadUrl = rawUrl,
                    .Origin = If(source.IsOfficial, PluginOrigin.Official, PluginOrigin.ThirdParty),
                    .SourceId = source.SourceId,
                    .SourceDisplayName = source.DisplayName,
                    .SourceOwner = source.Owner
                })
            Next

            result.Ok = True
            Return result
        End Function

        Private Sub StampFetched(sourceId As String)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim row = db.PluginSources.FirstOrDefault(Function(s) s.SourceId = sourceId)
                    If row IsNot Nothing Then
                        row.LastFetchedUtc = DateTime.UtcNow
                        db.SaveChanges()
                    End If
                End Using
            Catch
            End Try
        End Sub

        Private Shared Function GetStr(el As JsonElement, name As String) As String
            Dim v As JsonElement
            If el.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String Then Return v.GetString()
            Return Nothing
        End Function

    End Class

End Namespace
