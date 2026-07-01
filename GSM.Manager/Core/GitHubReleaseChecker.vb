Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.Http
Imports System.Reflection
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports GSM.Manager.Data

' ============================================================
'  GitHubReleaseChecker — Phase 5l-1 (detection + notification)
'
'  Polls the project's GitHub Releases on a fixed interval and
'  on demand, parses each release tag as a semantic version,
'  and compares the highest applicable release against the
'  running Manager's own version. Pure read operation: the only
'  state it mutates is a handful of rows in the AppSettings
'  key-value bag (last-check timestamp, latest version seen,
'  cached release notes/url). No new table, no migration.
'
'  Lifecycle mirrors VersionCheckService / ChatRetentionPruner:
'  Start() launches a background loop, StopAsync() cancels it.
'  Started only outside safe mode (ManagerProgram gates it with
'  the other background services).
'
'  Version model: the running version comes from the assembly's
'  InformationalVersion (which carries any "-rc1" pre-release
'  suffix), falling back to AssemblyVersion. Release tags ("v0.4.0",
'  "v0.4.0-rc1") are parsed the same way. System.Version can't
'  represent a pre-release suffix, so SemanticVersion (below)
'  wraps a Version core with a pre-release string and implements
'  semver precedence (a release outranks the matching pre-release;
'  rc1 < rc2; etc.).
'
'  GitHub rate limit: unauthenticated API allows 60 req/hr/IP.
'  Default 4-hour interval is ~6/day — far under budget, leaving
'  headroom for manual "Check for updates" clicks.
' ============================================================

Namespace GSM.Manager.Core

    Public Class GitHubReleaseChecker

        Private ReadOnly _serviceProvider As IServiceProvider
        Private ReadOnly _logger As ILogger(Of GitHubReleaseChecker)
        Private ReadOnly _http As HttpClient
        Private ReadOnly _runningVersion As SemanticVersion

        Private _cts As CancellationTokenSource
        Private _task As Task

        ''' <summary>
        ''' Raised after every completed check (success or failure),
        ''' on a background thread. UI subscribers must marshal to the
        ''' UI thread before touching controls.
        ''' </summary>
        Public Event StatusChanged As EventHandler(Of UpdateStatus)

        ' Let other DI services finish their own startup work first.
        Private Const StartupDelayMs As Integer = 15 * 1000

        Public Sub New(serviceProvider As IServiceProvider,
                       logger As ILogger(Of GitHubReleaseChecker))
            _serviceProvider = serviceProvider
            _logger = logger
            _runningVersion = GetRunningVersion()

            _http = New HttpClient() With {.Timeout = TimeSpan.FromSeconds(30)}
            ' GitHub returns 403 to requests without a User-Agent.
            Try
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM/" & _runningVersion.Core.ToString())
            Catch
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM")
            End Try
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
        End Sub

        ''' <summary>The running Manager's version, as a string.</summary>
        Public ReadOnly Property RunningVersion As String
            Get
                Return _runningVersion.ToString()
            End Get
        End Property

        ' ============================================================
        '  Lifecycle
        ' ============================================================

        ''' <summary>Starts the background polling loop. Idempotent.</summary>
        Public Sub Start()
            If _cts IsNot Nothing Then Return
            _cts = New CancellationTokenSource()
            Dim token = _cts.Token
            _task = Task.Run(Function() RunAsync(token))
            _logger.LogInformation(
                "GitHubReleaseChecker started (running version {Ver})", _runningVersion.ToString())
        End Sub

        ''' <summary>Cancels the loop and awaits it. Called from shutdown.</summary>
        Public Async Function StopAsync() As Task
            Dim cts = _cts
            If cts Is Nothing Then Return
            _cts = Nothing
            cts.Cancel()
            Try
                If _task IsNot Nothing Then Await _task
            Catch
            End Try
            cts.Dispose()
        End Function

        Private Async Function RunAsync(token As CancellationToken) As Task
            Try
                Await Task.Delay(StartupDelayMs, token)
            Catch
                Return
            End Try

            While Not token.IsCancellationRequested
                Dim intervalHours = ReadIntervalHours()
                Try
                    ' Restart-tolerance: don't re-poll on every Manager
                    ' restart if we already checked within the interval.
                    If RecentlyChecked(intervalHours) Then
                        _logger.LogDebug("GitHubReleaseChecker: checked recently, skipping this pass")
                    Else
                        Await CheckNowAsync(token)
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "GitHubReleaseChecker pass threw")
                End Try

                Try
                    Await Task.Delay(TimeSpan.FromHours(Math.Max(1, intervalHours)), token)
                Catch
                    Return
                End Try
            End While
        End Function

        ' ============================================================
        '  Check (one-shot; also used by the loop)
        ' ============================================================

        ''' <summary>
        ''' Runs one check now, bypassing the interval throttle.
        ''' Persists the result and raises StatusChanged. Never throws
        ''' — failures come back in the returned status's ErrorMessage.
        ''' </summary>
        Public Async Function CheckNowAsync(token As CancellationToken) As Task(Of UpdateStatus)
            Dim status As New UpdateStatus With {.CurrentVersion = _runningVersion.ToString()}

            Dim source As String = GsmDataExtensions.DefaultUpdateSource
            Dim includePre As Boolean = False
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    source = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateSource, GsmDataExtensions.DefaultUpdateSource)
                    includePre = db.GetSettingBool(GsmDataExtensions.SettingKeys.UpdateIncludePrereleases, False)
                    status.SkippedVersion = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateSkippedVersion, "")
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "GitHubReleaseChecker: reading settings failed; using defaults")
            End Try
            If String.IsNullOrWhiteSpace(source) Then source = GsmDataExtensions.DefaultUpdateSource

            Dim releases As List(Of GitHubRelease)
            Try
                releases = Await FetchReleasesAsync(source, token)
            Catch ex As Exception
                status.ErrorMessage = ex.Message
                _logger.LogWarning(ex, "GitHubReleaseChecker: fetch failed for {Source}", source)
                RaiseEvent StatusChanged(Me, status)
                Return status
            End Try

            ' Highest applicable release wins (Decision 2): drafts are
            ' always excluded; pre-releases excluded unless opted in.
            Dim best As SemanticVersion = Nothing
            Dim bestRelease As GitHubRelease = Nothing
            For Each rel In releases
                If rel Is Nothing OrElse rel.Draft Then Continue For
                If rel.Prerelease AndAlso Not includePre Then Continue For
                Dim sv = SemanticVersion.TryParse(rel.TagName)
                If sv Is Nothing Then
                    _logger.LogDebug("GitHubReleaseChecker: skipping unparseable tag {Tag}", rel.TagName)
                    Continue For
                End If
                If best Is Nothing OrElse sv.IsNewerThan(best) Then
                    best = sv
                    bestRelease = rel
                End If
            Next

            Dim checkedAt = DateTime.UtcNow
            If best Is Nothing Then
                status.LatestVersion = ""
                status.IsUpdateAvailable = False
                status.LastCheckUtc = checkedAt
                PersistState(checkedAt, status)
                _logger.LogInformation("GitHubReleaseChecker: no applicable release found at {Source}", source)
                RaiseEvent StatusChanged(Me, status)
                Return status
            End If

            status.LatestVersion = best.ToString()
            status.LatestTag = bestRelease.TagName
            status.IsPrerelease = bestRelease.Prerelease
            status.ReleaseBody = If(bestRelease.Body, "")
            status.ReleaseUrl = If(bestRelease.HtmlUrl, "")
            status.IsUpdateAvailable = best.IsNewerThan(_runningVersion)
            status.LastCheckUtc = checkedAt

            PersistState(checkedAt, status)

            If status.IsUpdateAvailable Then
                _logger.LogInformation(
                    "GitHubReleaseChecker: update available — running {Cur}, latest {Latest} ({Tag}, prerelease={Pre})",
                    _runningVersion.ToString(), best.ToString(), bestRelease.TagName, bestRelease.Prerelease)
            Else
                _logger.LogInformation(
                    "GitHubReleaseChecker: up to date — running {Cur}, latest {Latest}",
                    _runningVersion.ToString(), best.ToString())
            End If

            RaiseEvent StatusChanged(Me, status)
            Return status
        End Function

        ''' <summary>
        ''' Reads the last persisted check result without polling, and
        ''' recomputes IsUpdateAvailable against the running version.
        ''' For the UI to render the indicator on startup. The caller
        ''' decides whether to suppress the indicator when LatestVersion
        ''' equals SkippedVersion.
        ''' </summary>
        Public Function GetPersistedStatus() As UpdateStatus
            Dim status As New UpdateStatus With {.CurrentVersion = _runningVersion.ToString()}
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    status.LatestVersion = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateLatestVersion, "")
                    status.LatestTag = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateLatestTag, "")
                    status.ReleaseBody = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateReleaseBody, "")
                    status.ReleaseUrl = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateReleaseUrl, "")
                    status.SkippedVersion = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateSkippedVersion, "")
                    Dim lastStr = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateLastCheckUtc, "")
                    Dim last As DateTime
                    If Not String.IsNullOrEmpty(lastStr) AndAlso
                       DateTime.TryParse(lastStr, Nothing, Globalization.DateTimeStyles.RoundtripKind, last) Then
                        status.LastCheckUtc = last
                    End If
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "GitHubReleaseChecker: reading persisted status failed")
            End Try
            Dim latestSv = SemanticVersion.TryParse(status.LatestVersion)
            status.IsUpdateAvailable = latestSv IsNot Nothing AndAlso latestSv.IsNewerThan(_runningVersion)
            Return status
        End Function

        ' ============================================================
        '  Internals
        ' ============================================================

        Private Async Function FetchReleasesAsync(source As String,
                                                  token As CancellationToken) As Task(Of List(Of GitHubRelease))
            Dim url = $"https://api.github.com/repos/{source}/releases?per_page=30"
            Using resp = Await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                If Not resp.IsSuccessStatusCode Then
                    Dim reason = $"GitHub API returned {CInt(resp.StatusCode)} {resp.ReasonPhrase} for {source}."
                    If resp.StatusCode = System.Net.HttpStatusCode.Forbidden Then
                        reason &= " This may be rate-limiting (the unauthenticated GitHub API allows 60 requests per hour)."
                    End If
                    Throw New InvalidOperationException(reason)
                End If
                Dim json = Await resp.Content.ReadAsStringAsync(token)
                Dim opts As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
                Dim list = JsonSerializer.Deserialize(Of List(Of GitHubRelease))(json, opts)
                Return If(list, New List(Of GitHubRelease))
            End Using
        End Function

        Private Sub PersistState(checkedAt As DateTime, status As UpdateStatus)
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateLastCheckUtc, checkedAt.ToString("o"))
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateLatestVersion, If(status.LatestVersion, ""))
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateLatestTag, If(status.LatestTag, ""))
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateReleaseBody, If(status.ReleaseBody, ""))
                    db.SetSetting(GsmDataExtensions.SettingKeys.UpdateReleaseUrl, If(status.ReleaseUrl, ""))
                    db.SaveChanges()
                End Using
            Catch ex As Exception
                _logger.LogDebug(ex, "GitHubReleaseChecker: persisting state failed")
            End Try
        End Sub

        Private Function ReadIntervalHours() As Integer
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Return Math.Max(1, db.GetSettingInt(
                        GsmDataExtensions.SettingKeys.UpdateCheckIntervalHours,
                        GsmDataExtensions.DefaultUpdateCheckIntervalHours))
                End Using
            Catch
                Return GsmDataExtensions.DefaultUpdateCheckIntervalHours
            End Try
        End Function

        Private Function RecentlyChecked(intervalHours As Integer) As Boolean
            Try
                Using scope = _serviceProvider.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of GsmDbContext)()
                    Dim s = db.GetSetting(GsmDataExtensions.SettingKeys.UpdateLastCheckUtc, "")
                    If String.IsNullOrEmpty(s) Then Return False
                    Dim last As DateTime
                    If Not DateTime.TryParse(s, Nothing, Globalization.DateTimeStyles.RoundtripKind, last) Then Return False
                    Return (DateTime.UtcNow - last) < TimeSpan.FromHours(intervalHours)
                End Using
            Catch
                Return False
            End Try
        End Function

        Private Shared Function GetRunningVersion() As SemanticVersion
            Dim asm = Assembly.GetExecutingAssembly()
            Dim info = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
            If info IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(info.InformationalVersion) Then
                Dim parsed = SemanticVersion.TryParse(info.InformationalVersion)
                If parsed IsNot Nothing Then Return parsed
            End If
            Dim v = asm.GetName().Version
            If v IsNot Nothing Then
                Return New SemanticVersion(New Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build)), "", v.ToString())
            End If
            Return New SemanticVersion(New Version(0, 0, 0), "", "0.0.0")
        End Function

    End Class

    ' ============================================================
    '  Release DTO (System.Text.Json)
    ' ============================================================

    Friend Class GitHubRelease
        <JsonPropertyName("tag_name")>
        Public Property TagName As String
        <JsonPropertyName("name")>
        Public Property Name As String
        <JsonPropertyName("body")>
        Public Property Body As String
        <JsonPropertyName("html_url")>
        Public Property HtmlUrl As String
        <JsonPropertyName("prerelease")>
        Public Property Prerelease As Boolean
        <JsonPropertyName("draft")>
        Public Property Draft As Boolean
    End Class

    ' ============================================================
    '  Update status (returned by checks, read by the UI)
    ' ============================================================

    Public Class UpdateStatus
        ''' <summary>Running Manager version (semver string).</summary>
        Public Property CurrentVersion As String
        ''' <summary>Highest applicable release version, "" if none/unknown.</summary>
        Public Property LatestVersion As String = ""
        ''' <summary>Raw release tag (keeps the leading "v").</summary>
        Public Property LatestTag As String = ""
        ''' <summary>True when LatestVersion is newer than CurrentVersion.</summary>
        Public Property IsUpdateAvailable As Boolean
        ''' <summary>True when the latest applicable release is a pre-release.</summary>
        Public Property IsPrerelease As Boolean
        ''' <summary>Release notes body (plain text).</summary>
        Public Property ReleaseBody As String = ""
        ''' <summary>Release page URL (for a "view on GitHub" link).</summary>
        Public Property ReleaseUrl As String = ""
        ''' <summary>Version the user chose to skip, "" if none.</summary>
        Public Property SkippedVersion As String = ""
        ''' <summary>When the check ran (UTC).</summary>
        Public Property LastCheckUtc As DateTime?
        ''' <summary>Set on failure; Nothing/empty on success.</summary>
        Public Property ErrorMessage As String

        Public ReadOnly Property CheckSucceeded As Boolean
            Get
                Return String.IsNullOrEmpty(ErrorMessage)
            End Get
        End Property
    End Class

    ' ============================================================
    '  SemanticVersion — Version core + pre-release suffix, with
    '  semver precedence. System.Version alone can't model "-rc1".
    ' ============================================================

    Public NotInheritable Class SemanticVersion
        Implements IComparable(Of SemanticVersion)

        Private ReadOnly _core As Version
        Private ReadOnly _pre As String
        Private ReadOnly _raw As String

        Public ReadOnly Property Core As Version
            Get
                Return _core
            End Get
        End Property
        Public ReadOnly Property PreRelease As String
            Get
                Return _pre
            End Get
        End Property

        Public Sub New(core As Version, preRelease As String, raw As String)
            _core = core
            _pre = If(preRelease, "")
            _raw = If(raw, core.ToString())
        End Sub

        ''' <summary>
        ''' Parse "v0.4.0", "0.4.0", "0.4.0-rc1", "0.4.0-rc.2+sha"
        ''' etc. Strips a leading "v" and any "+build" metadata.
        ''' Returns Nothing if the numeric core doesn't parse.
        ''' </summary>
        Public Shared Function TryParse(text As String) As SemanticVersion
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim raw = text.Trim()
            Dim s = raw
            If s.StartsWith("v", StringComparison.OrdinalIgnoreCase) Then s = s.Substring(1)

            ' Drop build metadata (everything after the first '+').
            Dim plus = s.IndexOf("+"c)
            If plus >= 0 Then s = s.Substring(0, plus)

            ' Split off the pre-release suffix (everything after the first '-').
            Dim pre As String = ""
            Dim dash = s.IndexOf("-"c)
            If dash >= 0 Then
                pre = s.Substring(dash + 1)
                s = s.Substring(0, dash)
            End If

            Dim parts = s.Split("."c)
            Dim nums As New List(Of Integer)
            For Each p In parts
                Dim n As Integer
                If Not Integer.TryParse(p, n) Then Return Nothing
                If n < 0 Then Return Nothing
                nums.Add(n)
            Next
            While nums.Count < 3
                nums.Add(0)
            End While

            Dim core As Version
            Try
                core = New Version(nums(0), nums(1), nums(2))
            Catch
                Return Nothing
            End Try
            Return New SemanticVersion(core, pre, raw)
        End Function

        Public Function IsNewerThan(other As SemanticVersion) As Boolean
            Return CompareTo(other) > 0
        End Function

        Public Function CompareTo(other As SemanticVersion) As Integer Implements IComparable(Of SemanticVersion).CompareTo
            If other Is Nothing Then Return 1
            Dim c = _core.CompareTo(other._core)
            If c <> 0 Then Return c

            ' Equal numeric core: a release outranks a pre-release.
            Dim aPre = String.IsNullOrEmpty(_pre)
            Dim bPre = String.IsNullOrEmpty(other._pre)
            If aPre AndAlso bPre Then Return 0
            If aPre Then Return 1
            If bPre Then Return -1
            Return ComparePreRelease(_pre, other._pre)
        End Function

        ''' <summary>
        ''' Semver pre-release precedence: dot-separated identifiers
        ''' compared left to right. Numeric identifiers compare
        ''' numerically and rank below alphanumeric ones; a shorter
        ''' identifier set ranks below a longer one when all shared
        ''' identifiers are equal.
        ''' </summary>
        Private Shared Function ComparePreRelease(a As String, b As String) As Integer
            Dim ai = a.Split("."c)
            Dim bi = b.Split("."c)
            Dim n = Math.Max(ai.Length, bi.Length)
            For i = 0 To n - 1
                If i >= ai.Length Then Return -1
                If i >= bi.Length Then Return 1
                Dim x = ai(i)
                Dim y = bi(i)
                Dim xn As Integer, yn As Integer
                Dim xIsNum = Integer.TryParse(x, xn)
                Dim yIsNum = Integer.TryParse(y, yn)
                If xIsNum AndAlso yIsNum Then
                    Dim cc = xn.CompareTo(yn)
                    If cc <> 0 Then Return cc
                ElseIf xIsNum <> yIsNum Then
                    ' Numeric identifiers have lower precedence.
                    Return If(xIsNum, -1, 1)
                Else
                    Dim cc = String.CompareOrdinal(x, y)
                    If cc <> 0 Then Return Math.Sign(cc)
                End If
            Next
            Return 0
        End Function

        Public Overrides Function ToString() As String
            Return _raw
        End Function

    End Class

End Namespace
