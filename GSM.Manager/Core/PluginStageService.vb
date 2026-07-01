Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data

' ============================================================
'  PluginStageService — Phase 6-3
'
'  Downloads a chosen catalog entry's .vb source into a staging
'  area, validates it there, and records the staged state — all
'  WITHOUT touching the live Plugins\ folder. 6-4's install/apply
'  is then just "move the staged file into Plugins\ + ReloadAll".
'
'  Staging layout:  <install>\.plugin-updates\{pluginId}\{FileName}
'
'  A mid-download restart can only ever leave a partial file in
'  staging (which a later stage of the same plugin overwrites);
'  the live Plugins\ folder is never written by this service.
'
'  Validation performed at stage time:
'    - download is non-trivial (error-page guard) and parses to a
'      manifest with an id and a version
'    - the staged manifest id matches the catalog entry it was
'      staged from
'    - naming/prefix rule (Decision 2): plugins from a NON-official
'      source are expected to use an "{owner}_..." id (owner = the
'      SOURCE's GitHub owner, not the spoofable manifest author).
'      A bare id from a non-official source, or an id that collides
'      with an already-loaded plugin, produces a WARNING the UI
'      must confirm — never a hard block, never a silent shadow.
'    - declared dependencies (Decision 7): resolved shallowly
'      against (a) already-loaded plugins, (b) the same source's
'      catalog, (c) the official source's catalog. A missing or
'      too-old dependency BLOCKS the stage with a message naming
'      what's needed.
'
'  Staged state persists as a JSON list in the AppSettings key
'  "plugins.staged" so it survives a Manager restart. Mirrors
'  UpdateOrchestrator's result-object discipline: never throws to
'  the caller.
' ============================================================

Namespace GSM.Manager.Core

    ''' <summary>One staged plugin awaiting install (6-4).</summary>
    Public NotInheritable Class StagedPlugin
        Public Property PluginId As String
        Public Property FileName As String
        Public Property Version As String
        Public Property SourceId As String
        Public Property SourceOwner As String
        Public Property IsOfficialSource As Boolean
        Public Property StagedPath As String
        Public Property StagedAtUtc As DateTime
        ''' <summary>Naming/collision warnings the user must see and
        ''' confirm before 6-4 installs this plugin. Empty = clean.</summary>
        Public Property Warnings As New List(Of String)
        ''' <summary>Phase 7-3 — capabilities the plugin's manifest
        ''' declares (`requires` attribute). Shown in the install/
        ''' update consent so the operator approves knowing what the
        ''' plugin says it does. Empty for plugins that declare none.</summary>
        Public Property Capabilities As New List(Of String)
        ''' <summary>Phase 7-3b — advisory titles from the static
        ''' source audit (P/Invoke, Process.Start, reflection,
        ''' undeclared-network). Shown in the install/update consent
        ''' alongside capabilities. Advisory only — never blocks.</summary>
        Public Property AuditNotes As New List(Of String)
    End Class

    ''' <summary>Result of a plugin stage attempt. (Named distinctly
    ''' from UpdateOrchestrator's StageResult — same namespace.)</summary>
    Public NotInheritable Class PluginStageResult
        Public Property Ok As Boolean
        ''' <summary>Why the stage was blocked (download/parse/dependency
        ''' failure). Nothing when Ok.</summary>
        Public Property ErrorMessage As String
        Public Property Staged As StagedPlugin
    End Class

    ''' <summary>Result of installing a staged plugin (6-4).</summary>
    Public NotInheritable Class PluginInstallResult
        Public Property Ok As Boolean
        Public Property ErrorMessage As String
        ''' <summary>The file's final path inside Plugins\.</summary>
        Public Property InstalledPath As String
    End Class

    ''' <summary>One installed plugin with a newer catalog version (6-4).</summary>
    Public NotInheritable Class PluginUpdateInfo
        Public Property PluginId As String
        Public Property InstalledVersion As String
        Public Property LatestVersion As String
        ''' <summary>The catalog entry offering the newer version —
        ''' feed it straight to StageAsync to update.</summary>
        Public Property Entry As CatalogEntry
    End Class

    Public Class PluginStageService

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of PluginStageService)
        Private ReadOnly _registry As PluginRegistry
        Private ReadOnly _catalog As PluginCatalogService
        Private ReadOnly _http As HttpClient

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of PluginStageService),
                       registry As PluginRegistry,
                       catalog As PluginCatalogService)
            _serviceProvider = serviceProvider
            _logger = logger
            _registry = registry
            _catalog = catalog
            _http = New HttpClient() With {.Timeout = TimeSpan.FromSeconds(60)}
            Try
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM")
            Catch
            End Try
        End Sub

        ''' <summary>Root of the staging area, beside the install.</summary>
        Public Shared Function StagingRoot() As String
            Return Path.Combine(AppContext.BaseDirectory, ".plugin-updates")
        End Function

        ' ============================================================
        '  Stage
        ' ============================================================

        ''' <summary>
        ''' Download + validate one catalog entry into staging. Never
        ''' throws; never touches Plugins\.
        ''' </summary>
        Public Async Function StageAsync(entry As CatalogEntry,
                                         Optional ct As CancellationToken = Nothing) As Task(Of PluginStageResult)
            Dim r As New PluginStageResult()
            If entry Is Nothing OrElse entry.Manifest Is Nothing Then
                r.ErrorMessage = "Nothing to stage."
                Return r
            End If

            Dim m = entry.Manifest
            If String.IsNullOrEmpty(m.Id) Then
                r.ErrorMessage = "The plugin's manifest has no id — it can't be staged."
                Return r
            End If
            If String.IsNullOrEmpty(m.Version) Then
                r.ErrorMessage = "The plugin's manifest has no version — unversioned plugins can't be staged (the catalog can't track updates for them)."
                Return r
            End If

            ' 1) Download the full source text.
            Dim text As String
            Try
                text = Await _http.GetStringAsync(entry.DownloadUrl, ct)
            Catch ex As Exception
                r.ErrorMessage = $"Download failed: {ex.Message}"
                Return r
            End Try
            If text Is Nothing OrElse text.Length < 200 Then
                ' A real plugin source is thousands of chars; anything
                ' this small is an error page or a stub.
                r.ErrorMessage = "The downloaded file is too small to be a plugin source — it may be an error page."
                Return r
            End If

            ' 2) Re-parse the downloaded text — the authoritative copy —
            '    and confirm it matches the catalog entry that was chosen.
            Dim staged = PluginManifestParser.Parse(text)
            If staged Is Nothing OrElse Not staged.HasPluginBlock OrElse String.IsNullOrEmpty(staged.Id) Then
                r.ErrorMessage = "The downloaded file has no <plugin> manifest — it doesn't look like a managed plugin."
                Return r
            End If
            If Not String.Equals(staged.Id, m.Id, StringComparison.OrdinalIgnoreCase) Then
                r.ErrorMessage = $"The downloaded file declares id ""{staged.Id}"" but the catalog entry was ""{m.Id}"" — refusing to stage a mismatched plugin."
                Return r
            End If

            ' 3) Dependency resolution (Decision 7) — blocking.
            Dim depError = Await CheckDependenciesAsync(staged, entry, ct)
            If depError IsNot Nothing Then
                r.ErrorMessage = depError
                Return r
            End If

            ' 4) Naming/prefix + collision checks (Decision 2) — warnings.
            Dim warnings = BuildNamingWarnings(staged, entry)

            ' 4b) Static source audit (7-3b) — advisory notes for the
            ' install consent. Never blocks; the operator decides.
            Dim auditFindings = PluginSourceAudit.Scan(text, staged.Requires)

            ' 5) Write to staging.
            Dim stagedDir = Path.Combine(StagingRoot(), SafePathSegment(staged.Id))
            Dim stagedPath = Path.Combine(stagedDir, entry.FileName)
            Try
                Directory.CreateDirectory(stagedDir)
                File.WriteAllText(stagedPath, text)
            Catch ex As Exception
                r.ErrorMessage = $"Couldn't write to the staging folder: {ex.Message}"
                Return r
            End Try

            ' 6) Record the staged state.
            Dim sp As New StagedPlugin With {
                .PluginId = staged.Id,
                .FileName = entry.FileName,
                .Version = staged.Version,
                .SourceId = entry.SourceId,
                .SourceOwner = entry.SourceOwner,
                .IsOfficialSource = (entry.Origin = PluginOrigin.Official),
                .StagedPath = stagedPath,
                .StagedAtUtc = DateTime.UtcNow,
                .Warnings = warnings,
                .Capabilities = If(staged.Requires, New List(Of String)),
                .AuditNotes = PluginSourceAudit.ToConsentLines(auditFindings)
            }
            UpsertStagedState(sp)

            _logger.LogInformation("Staged plugin {Id} {Version} from {Owner} to {Path}",
                                   sp.PluginId, sp.Version, sp.SourceOwner, sp.StagedPath)
            r.Ok = True
            r.Staged = sp
            Return r
        End Function

        ''' <summary>
        ''' Decision 7 — shallow dependency check. Each declared
        ''' dependency must exist (loaded, or available from the same
        ''' source / the official source) at >= the declared min
        ''' version. Returns a blocking error message, or Nothing when
        ''' satisfied. Dependencies that are merely *available* (not
        ''' loaded) satisfy the check but are named in the message the
        ''' UI shows — installing them stays a user action in v1.
        ''' </summary>
        Private Async Function CheckDependenciesAsync(staged As PluginManifest,
                                                      entry As CatalogEntry,
                                                      ct As CancellationToken) As Task(Of String)
            If staged.Dependencies Is Nothing OrElse staged.Dependencies.Count = 0 Then Return Nothing

            ' Lazily-fetched catalogs: same source first, official as
            ' the fallback pool.
            Dim catalogs As List(Of CatalogEntry) = Nothing

            For Each dep In staged.Dependencies
                If String.IsNullOrEmpty(dep.Id) Then Continue For
                Dim minVer As SemanticVersion = Nothing
                If Not String.IsNullOrEmpty(dep.Min) Then minVer = SemanticVersion.TryParse(dep.Min)

                ' (a) Already loaded?
                Dim loadedManifest = _registry.GetManifest(dep.Id)
                If loadedManifest IsNot Nothing Then
                    If minVer Is Nothing Then Continue For
                    Dim loadedVer = SemanticVersion.TryParse(loadedManifest.Version)
                    If loadedVer IsNot Nothing AndAlso loadedVer.CompareTo(minVer) >= 0 Then Continue For
                    Return $"This plugin needs ""{dep.Id}"" version {dep.Min} or newer, but version {If(loadedManifest.Version, "unknown")} is installed. Update ""{dep.Id}"" first."
                End If

                ' (b)/(c) Available from the same source or official?
                If catalogs Is Nothing Then catalogs = Await GatherDependencyPoolAsync(entry, ct)
                Dim candidate = catalogs.
                    Where(Function(c) c.Manifest IsNot Nothing AndAlso
                                      String.Equals(c.Manifest.Id, dep.Id, StringComparison.OrdinalIgnoreCase)).
                    FirstOrDefault()
                If candidate Is Nothing Then
                    Return $"This plugin depends on ""{dep.Id}""{If(dep.Min IsNot Nothing, $" (>= {dep.Min})", "")}, which isn't installed and wasn't found in this source or the official source."
                End If
                If minVer IsNot Nothing Then
                    Dim candVer = SemanticVersion.TryParse(candidate.Manifest.Version)
                    If candVer Is Nothing OrElse candVer.CompareTo(minVer) < 0 Then
                        Return $"This plugin needs ""{dep.Id}"" {dep.Min} or newer; the catalog only offers {If(candidate.Manifest.Version, "an unversioned copy")}."
                    End If
                End If
                ' Available but not installed — allowed; the install UI
                ' (6-4) tells the user to install the dependency first.
                Return $"This plugin depends on ""{dep.Id}"", which is available from the catalog but not installed. Install ""{dep.Id}"" first, then stage this plugin again."
            Next
            Return Nothing
        End Function

        Private Async Function GatherDependencyPoolAsync(entry As CatalogEntry,
                                                         ct As CancellationToken) As Task(Of List(Of CatalogEntry))
            Dim pool As New List(Of CatalogEntry)
            Try
                Dim sources = _catalog.GetSources()
                Dim same = sources.FirstOrDefault(Function(s) s.SourceId = entry.SourceId)
                Dim official = sources.FirstOrDefault(Function(s) s.IsOfficial)
                For Each src In {same, official}
                    If src Is Nothing OrElse Not src.IsEnabled Then Continue For
                    If pool.Count > 0 AndAlso same IsNot Nothing AndAlso official IsNot Nothing AndAlso
                       same.SourceId = official.SourceId Then Exit For
                    Dim cat = Await _catalog.GetCatalogAsync(src, ct:=ct)
                    If cat IsNot Nothing AndAlso cat.Ok Then pool.AddRange(cat.Entries)
                Next
            Catch ex As Exception
                _logger.LogDebug("Dependency pool fetch failed: {Msg}", ex.Message)
            End Try
            Return pool
        End Function

        ''' <summary>
        ''' Decision 2 — naming/prefix + collision warnings. Warnings,
        ''' not blocks: the UI shows them and requires explicit consent
        ''' before installing.
        ''' </summary>
        Private Function BuildNamingWarnings(staged As PluginManifest, entry As CatalogEntry) As List(Of String)
            Dim warnings As New List(Of String)

            If entry.Origin <> PluginOrigin.Official Then
                Dim expectedPrefix = (If(entry.SourceOwner, "")).ToLowerInvariant() & "_"
                If Not staged.Id.ToLowerInvariant().StartsWith(expectedPrefix, StringComparison.Ordinal) Then
                    warnings.Add(
                        $"Third-party plugins are expected to use an id prefixed with their source's owner (""{expectedPrefix}…""), " &
                        $"but this one declares ""{staged.Id}"". A bare id can shadow or collide with an official plugin of the same name.")
                End If
            End If

            ' Collision with an already-loaded plugin (any origin).
            ' Installing over the same id replaces what's loaded —
            ' fine when it's an update of the same plugin, dangerous
            ' when it's a different plugin claiming the same id.
            Dim existing = _registry.GetManifest(staged.Id)
            If existing IsNot Nothing Then
                Dim existingDesc = If(existing.HasPluginBlock,
                                      $"version {If(existing.Version, "unknown")} by {If(existing.Author, "unknown")}",
                                      "an unmanaged local plugin")
                warnings.Add(
                    $"A plugin with id ""{staged.Id}"" is already installed ({existingDesc}). Installing this will replace it.")
            End If

            Return warnings
        End Function

        ' ============================================================
        '  Install (6-4)
        ' ============================================================

        ''' <summary>
        ''' Move a staged plugin into the live Plugins\ folder. The
        ''' caller is responsible for (a) having shown the staged
        ''' warnings and obtained consent, and (b) reloading plugins
        ''' afterwards — install itself is just the file move + staged-
        ''' state cleanup, so it stays UI-free. Copy-then-discard (not
        ''' Move) so a failure can't lose the staged copy.
        ''' </summary>
        Public Function InstallStaged(pluginId As String) As PluginInstallResult
            Dim r As New PluginInstallResult()
            Dim staged = GetStagedFor(pluginId)
            If staged Is Nothing Then
                r.ErrorMessage = $"""{pluginId}"" isn't staged — download it first."
                Return r
            End If
            If Not File.Exists(staged.StagedPath) Then
                r.ErrorMessage = $"The staged file for ""{pluginId}"" is missing ({staged.StagedPath}). Download it again."
                DiscardStaged(pluginId)
                Return r
            End If

            Try
                Dim pluginsDir = _registry.PluginsDirectory
                Directory.CreateDirectory(pluginsDir)
                Dim dest = Path.Combine(pluginsDir, staged.FileName)
                File.Copy(staged.StagedPath, dest, overwrite:=True)
                r.InstalledPath = dest
            Catch ex As Exception
                r.ErrorMessage = $"Couldn't copy the plugin into the Plugins folder: {ex.Message}"
                Return r
            End Try

            DiscardStaged(pluginId)
            _logger.LogInformation("Installed plugin {Id} {Version} to {Path}", pluginId, staged.Version, r.InstalledPath)
            r.Ok = True
            Return r
        End Function

        ' ============================================================
        '  Update detection + uninstall (6-4)
        ' ============================================================

        ''' <summary>
        ''' Decision 8 — compare every installed, version-carrying
        ''' plugin against the best version offered across all enabled
        ''' sources. Plugins without a manifest version or not present
        ''' in any catalog simply aren't tracked. Never throws; never
        ''' auto-applies anything.
        ''' </summary>
        Public Async Function CheckForUpdatesAsync(Optional forceRefresh As Boolean = False,
                                                   Optional ct As CancellationToken = Nothing) As Task(Of List(Of PluginUpdateInfo))
            Dim updates As New List(Of PluginUpdateInfo)
            Try
                ' Best catalog entry per plugin id across enabled sources.
                Dim best As New Dictionary(Of String, CatalogEntry)(StringComparer.OrdinalIgnoreCase)
                For Each src In _catalog.GetSources()
                    If Not src.IsEnabled Then Continue For
                    Dim cat = Await _catalog.GetCatalogAsync(src, forceRefresh, ct)
                    If cat Is Nothing OrElse Not cat.Ok Then Continue For
                    For Each entry In cat.Entries
                        Dim id = entry.Manifest?.Id
                        Dim ver = SemanticVersion.TryParse(entry.Manifest?.Version)
                        If id Is Nothing OrElse ver Is Nothing Then Continue For
                        Dim existing As CatalogEntry = Nothing
                        If Not best.TryGetValue(id, existing) Then
                            best(id) = entry
                        Else
                            Dim existingVer = SemanticVersion.TryParse(existing.Manifest.Version)
                            If existingVer Is Nothing OrElse ver.CompareTo(existingVer) > 0 Then best(id) = entry
                        End If
                    Next
                Next

                ' Compare against what's loaded.
                For Each gid In _registry.GetLoadedGameIds()
                    Dim installed = _registry.GetManifest(gid)
                    If installed Is Nothing OrElse Not installed.HasPluginBlock Then Continue For
                    Dim installedVer = SemanticVersion.TryParse(installed.Version)
                    If installedVer Is Nothing Then Continue For
                    Dim candidate As CatalogEntry = Nothing
                    If Not best.TryGetValue(installed.Id, candidate) Then Continue For
                    Dim candidateVer = SemanticVersion.TryParse(candidate.Manifest.Version)
                    If candidateVer IsNot Nothing AndAlso candidateVer.CompareTo(installedVer) > 0 Then
                        updates.Add(New PluginUpdateInfo With {
                            .PluginId = installed.Id,
                            .InstalledVersion = installed.Version,
                            .LatestVersion = candidate.Manifest.Version,
                            .Entry = candidate
                        })
                    End If
                Next
            Catch ex As Exception
                _logger.LogWarning(ex, "CheckForUpdatesAsync failed")
            End Try
            Return updates
        End Function

        ' ============================================================
        '  Staged-state persistence ("plugins.staged" JSON list)
        ' ============================================================

        ''' <summary>All currently-staged plugins (may be empty).</summary>
        Public Function GetStaged() As List(Of StagedPlugin)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim json = db.GetSetting(GsmDataExtensions.SettingKeys.PluginsStaged, "")
                    If String.IsNullOrWhiteSpace(json) Then Return New List(Of StagedPlugin)()
                    Dim list = JsonSerializer.Deserialize(Of List(Of StagedPlugin))(json)
                    Return If(list, New List(Of StagedPlugin)())
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "GetStaged failed")
                Return New List(Of StagedPlugin)()
            End Try
        End Function

        ''' <summary>The staged record for one plugin id, or Nothing.</summary>
        Public Function GetStagedFor(pluginId As String) As StagedPlugin
            If String.IsNullOrEmpty(pluginId) Then Return Nothing
            Return GetStaged().FirstOrDefault(
                Function(s) String.Equals(s.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Discard a staged plugin: delete its staging folder
        ''' and drop it from the staged list.</summary>
        Public Sub DiscardStaged(pluginId As String)
            If String.IsNullOrEmpty(pluginId) Then Return
            Try
                Dim dir = Path.Combine(StagingRoot(), SafePathSegment(pluginId))
                If Directory.Exists(dir) Then Directory.Delete(dir, recursive:=True)
            Catch ex As Exception
                _logger.LogWarning(ex, "DiscardStaged: couldn't delete staging folder for {Id}", pluginId)
            End Try
            Dim list = GetStaged()
            list.RemoveAll(Function(s) String.Equals(s.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            SaveStaged(list)
        End Sub

        ''' <summary>Insert or replace the staged record for a plugin.</summary>
        Private Sub UpsertStagedState(sp As StagedPlugin)
            Dim list = GetStaged()
            list.RemoveAll(Function(s) String.Equals(s.PluginId, sp.PluginId, StringComparison.OrdinalIgnoreCase))
            list.Add(sp)
            SaveStaged(list)
        End Sub

        Private Sub SaveStaged(list As List(Of StagedPlugin))
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    db.SetSetting(GsmDataExtensions.SettingKeys.PluginsStaged, JsonSerializer.Serialize(list))
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogWarning(ex, "SaveStaged failed")
            End Try
        End Sub

        ''' <summary>Keep ids safe as a single path segment.</summary>
        Private Shared Function SafePathSegment(id As String) As String
            Dim cleaned = id
            For Each c In Path.GetInvalidFileNameChars()
                cleaned = cleaned.Replace(c, "_"c)
            Next
            Return cleaned
        End Function

    End Class

End Namespace
