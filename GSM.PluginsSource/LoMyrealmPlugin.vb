' <plugin id="lo-myrealm" name="Last Oasis myrealm Names" version="1.0.0" author="siteml" requiresContracts="2" requires="events, identity-read, identity-write, notifications, network, config, web-capture">
' <RequiresContracts: 2>
Imports System
Imports System.Collections.Generic
Imports System.Net
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks
Imports GSM.Plugin
Imports GSM.Utility

' ============================================================
'  LoMyrealmPlugin — Last Oasis myrealm character-name resolution
'  (renamed from SteamSessionPlugin — the held session is a
'  myrealm/GPORTAL portal session, Steam is only the OpenID hop,
'  and the lookup is LO-specific.)
'
'  WHAT IT IS (and is not): a name-resolution helper, NOT a
'  SteamID fetcher. The CharacterId <-> SteamID64 <-> persona <->
'  display-name chain is already assembled by the LO parse rules +
'  IdentityResolver. myrealm's distinct value is the AUTHORITATIVE
'  current character name, read from the rename-character page's
'  Name textbox:
'
'    https://myrealm.lastoasis.gg/realm/{realm_id}/Characters/{character_id}/Rename
'
'  realm_id comes off the event's SessionIdentity
'  ("lastoasis:{realm_id}:{tile_id}"); character_id off the event.
'  This fills the naming window BEFORE the first Persisting tick
'  and tracks portal renames.
'
'  SESSION (Phase 7-5): the myrealm portal session is NOT held by
'  this plugin anymore. It lives in the Manager's shared web-session
'  store under key "myrealm:default", encrypted at rest, captured
'  via the host's embedded login dialog. The host owns persistence,
'  once-per-run prompt throttling, and in-flight dedup — this plugin
'  just asks for the cookie header (allowPrompt controls whether a
'  missing session may open the dialog) and calls InvalidateWebSession
'  on detecting expiry. Any other plugin using the same key shares
'  the session.
'
'  FLOW per PlayerJoin/PlayerLeave (LO only):
'    1. Skip fast when a name is known (evt.CharacterName,
'       double-checked via ResolveIdentity) — UNLESS this is a JOIN
'       and VerifyOnJoin is on, in which case re-read to catch portal
'       renames (unchanged = no write). Verification is join-only,
'       spaced >= 5 min/character, and never prompts for login
'       (allowPrompt:=False).
'    2. Skip fallback SessionIdentity ({gameId}:{instanceId}) —
'       realm_id must be numeric; those resolve later via Persisting.
'    3. Get the session header from the store (prompt when this is a
'       genuine gap and AutoPromptLogin is on).
'    4. GET the rename page (no auto-redirect). Redirect or served
'       sign-in page = expired -> InvalidateWebSession + notify once
'       -> next gap re-prompts. Defensive shape makes session
'       lifetime moot.
'    5. Parse the Name input, ContributeIdentity CharacterId ->
'       CharacterName scoped to the event's realm:tile.
'
'  Manual sign-in: "Sign in at next plugin reload" one-shot flag —
'  needed because the auto-prompt only fires on a genuine naming gap,
'  which never occurs on a realm the resolver already fully knows.
'  Invalidates the stored session then forces a prompted fetch, so it
'  doubles as a session refresh.
'
'  Per-CharacterId cooldowns: 30 min after a failed lookup, 5 min
'  between re-reads after success. Dispatch is sequential per plugin
'  (host queue), so no internal locking is needed.
' ============================================================

Public Class LoMyrealmPlugin
    Implements IUtilityPlugin, IWebSessionValidator, IWebPortalDataProvider

    Private Const SessionKey As String = "myrealm:default"
    Private Const RealmIdConfigKey As String = "myrealm.realmId"
    Private Const BaseUrl As String = "https://myrealm.lastoasis.gg"
    Private Shared ReadOnly FailureCooldown As TimeSpan = TimeSpan.FromMinutes(30)
    Private Shared ReadOnly VerifyCooldown As TimeSpan = TimeSpan.FromMinutes(5)

    Private _http As HttpClient
    Private _expiryNotified As Boolean
    ''' <summary>Holds the fire-and-forget manual sign-in task so
    ''' plugin reload isn't stalled behind the login window.</summary>
    Private _backgroundSignIn As Task
    ''' <summary>Per-CharacterId earliest-next-query time, written
    ''' after each lookup (now+30min on failure, now+5min on
    ''' success).</summary>
    Private ReadOnly _nextAllowedUtc As New Dictionary(Of String, DateTime)

    ''' <summary>realmId → the session key that last successfully read a
    ''' character name there (decision 5 self-healing failover cache).
    ''' Guarded by _realmSessionGate: event dispatch is sequential per
    ''' plugin, but lock to be safe per the plan.</summary>
    Private ReadOnly _realmSession As New Dictionary(Of String, String)
    Private ReadOnly _realmSessionGate As New Object()

    Public ReadOnly Property PluginId As String = "lo-myrealm" Implements IUtilityPlugin.PluginId

    Public ReadOnly Property DisplayName As String = "Last Oasis myrealm Names" Implements IUtilityPlugin.DisplayName

    Public ReadOnly Property SubscribedEvents As IReadOnlyList(Of UtilityEventKind) _
        Implements IUtilityPlugin.SubscribedEvents
        Get
            Return New UtilityEventKind() {
                UtilityEventKind.PlayerJoin,
                UtilityEventKind.PlayerLeave
            }
        End Get
    End Property

    Public Function GetConfigSchema() As List(Of ConfigFieldDescriptor) Implements IUtilityPlugin.GetConfigSchema
        Return New List(Of ConfigFieldDescriptor) From {
            New ConfigFieldDescriptor With {
                .Key = "Enabled",
                .Label = "Enable myrealm lookups",
                .Description = "Resolve un-named Last Oasis characters via the myrealm rename page.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "True"
            },
            New ConfigFieldDescriptor With {
                .Key = "AutoPromptLogin",
                .Label = "Prompt for myrealm login when needed",
                .Description = "When a lookup needs a session and none is stored, open the embedded " &
                               "login window automatically (at most once per Manager run). When off, " &
                               "the plugin only sends a notification.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "True"
            },
            New ConfigFieldDescriptor With {
                .Key = "VerifyOnJoin",
                .Label = "Re-check names on join",
                .Description = "On every player join, re-read the myrealm rename page for already-named " &
                               "characters to catch portal renames (at most once per 5 minutes per " &
                               "character; never prompts for login).",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "True"
            },
            New ConfigFieldDescriptor With {
                .Key = "SignInOnNextReload",
                .Label = "Sign in at next plugin reload",
                .Description = "One-shot manual trigger: tick this, save, then reload plugins — the " &
                               "myrealm login window opens immediately (replacing any stored session, " &
                               "so this also refreshes an old one). The flag clears itself.",
                .FieldType = ConfigFieldType.BooleanField,
                .DefaultValue = "False"
            }
        }
    End Function

    Public Function InitializeAsync(context As IUtilityContext) As Task Implements IUtilityPlugin.InitializeAsync
        ' AllowAutoRedirect=False: a redirect on the rename page is
        ' the expired-session signal, not something to follow.
        Dim handler As New HttpClientHandler With {
            .AllowAutoRedirect = False,
            .UseCookies = False
        }
        _http = New HttpClient(handler) With {
            .Timeout = TimeSpan.FromSeconds(15)
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerGSM/1.0")

        ' One-shot manual sign-in trigger. The flag clears BEFORE the
        ' capture starts so it stays one-shot even on cancel. Fire-and-
        ' forget into a field: not awaiting it keeps plugin reload from
        ' stalling behind the login window.
        If IsFlagSet(context, "SignInOnNextReload") Then
            context.SetConfigValue("SignInOnNextReload", "False")
            _backgroundSignIn = RunRequestedSignInAsync(context)
            context.LogInformation(
                "LoMyrealmPlugin initialised — manual sign-in requested; refreshing the myrealm session.")
        Else
            context.LogInformation(
                "LoMyrealmPlugin initialised — myrealm session is held by the Manager's shared store " &
                "(key '" & SessionKey & "'). The login prompt opens automatically on the first un-named " &
                "LO character; to sign in manually, tick 'Sign in at next plugin reload' in Configure and reload.")
        End If
        Return Task.CompletedTask
    End Function

    ''' <summary>Manual trigger: drop any stored session, then force a
    ''' prompted fetch so the login window opens now (doubles as a
    ''' refresh).</summary>
    Private Async Function RunRequestedSignInAsync(context As IUtilityContext) As Task
        Try
            context.InvalidateWebSession(SessionKey)
            Dim header = Await context.GetOrCaptureWebSessionAsync(
                SessionKey, BaseUrl & "/", "/customer/", "myrealm.lastoasis.gg", True)
            If Not String.IsNullOrEmpty(header) Then
                _expiryNotified = False
                Await LogDiscoveredRecordsAsync(context)
            End If
        Catch ex As Exception
            context.LogWarning("Requested myrealm sign-in failed: " & ex.Message)
        End Try
    End Function

    ''' <summary>7-6 slice-1 diagnostic: right after a manual sign-in,
    ''' run the full portal discovery and dump what we'd import to the
    ''' log. No writes anywhere - this only exercises the read-only
    ''' scrape against the live portal until the Manager-side import UI
    ''' (slice 2) drives it. CustomerKey is never logged (sensitive);
    ''' ProviderKey is truncated.</summary>
    Private Async Function LogDiscoveredRecordsAsync(context As IUtilityContext) As Task
        ' Session was just (re)captured by the caller — allowPrompt:=False
        ' reuses it without opening a second dialog.
        Dim records = Await DiscoverRecordsAsync(SessionKey, False, context)
        If records.Count = 0 Then
            context.LogInformation("Portal discovery: no importable realm records found.")
            Return
        End If
        context.LogInformation("Portal discovery: " & records.Count & " importable realm record(s):")
        For Each r In records
            Dim providerKey = r.Fields("ProviderKey")
            Dim providerShort As String = providerKey
            If providerKey IsNot Nothing AndAlso providerKey.Length > 4 Then
                providerShort = providerKey.Substring(0, 4) & "..."
            End If
            Dim realmName = r.Fields("RealmName")
            Dim realmLabel = If(String.IsNullOrEmpty(realmName), "(unnamed)", realmName)
            Dim usedByLabel = If(String.IsNullOrEmpty(r.UsedBy), "no label", r.UsedBy)
            context.LogInformation(
                "  realm " & r.SourceRef & " """ & realmLabel & """ - provider " & providerShort & " (" & usedByLabel & ")")
        Next
    End Function

    Public Async Function HandleEventAsync(evt As UtilityEvent, context As IUtilityContext) As Task _
        Implements IUtilityPlugin.HandleEventAsync

        If evt Is Nothing Then Return
        If Not String.Equals(evt.GameId, "lastoasis", StringComparison.OrdinalIgnoreCase) Then Return
        If String.IsNullOrEmpty(evt.CharacterId) Then Return
        If Not IsEnabled(context, "Enabled") Then Return

        ' Current best name: the event carries the resolver's answer
        ' at dispatch time; ResolveIdentity double-checks for races.
        Dim knownName = evt.CharacterName
        If String.IsNullOrEmpty(knownName) Then
            Dim known = context.ResolveIdentity(evt.CharacterId)
            If known IsNot Nothing Then knownName = known.CharacterName
        End If

        ' A named character normally needs nothing — unless this is a
        ' JOIN and VerifyOnJoin is on (re-read to catch portal renames).
        Dim verifying = Not String.IsNullOrEmpty(knownName)
        If verifying Then
            If evt.Kind <> UtilityEventKind.PlayerJoin Then Return
            If Not IsEnabled(context, "VerifyOnJoin") Then Return
        End If

        ' realm_id from SessionIdentity. Fallback identities carry the
        ' instanceId (non-numeric) as scope — skip; resolve later.
        Dim sessionScope As String = Nothing
        Dim realmId As String = Nothing
        If Not TryParseRealm(evt.SessionIdentity, sessionScope, realmId) Then
            If Not verifying Then
                context.LogInformation(
                    $"Skipping myrealm lookup for character {evt.CharacterId}: no realm_id on session '{evt.SessionIdentity}' (pre-tile-load join or fallback identity).")
            End If
            Return
        End If

        ' Per-CharacterId query spacing (written after each lookup).
        Dim nextAllowed As DateTime
        If _nextAllowedUtc.TryGetValue(evt.CharacterId, nextAllowed) AndAlso
           DateTime.UtcNow < nextAllowed Then Return

        ' Resolve via the realm's preferred session, failing over to any
        ' other live myrealm account that can reach the realm (decision
        ' 5). A genuine gap with prompting on may open ONE host-throttled
        ' login only when no account is stored at all; verification never
        ' prompts.
        Dim allowFirstPrompt = (Not verifying) AndAlso IsEnabled(context, "AutoPromptLogin")
        Dim characterName = Await ResolveNameWithFailoverAsync(
            realmId, evt.CharacterId, allowFirstPrompt, context)

        If String.IsNullOrEmpty(characterName) Then
            _nextAllowedUtc(evt.CharacterId) = DateTime.UtcNow + FailureCooldown
            ' No stored account could read this realm. Nudge once on a
            ' genuine gap with prompting off (covers the old "login needed"
            ' + "expired" cases — now only when EVERY account is down for
            ' this realm).
            If Not verifying AndAlso Not IsEnabled(context, "AutoPromptLogin") AndAlso Not _expiryNotified Then
                _expiryNotified = True
                Await context.SendNotificationAsync(
                    "myrealm lookup needs an account",
                    "An un-named Last Oasis character was seen, but no stored myrealm account could read " &
                    "its realm. Add or refresh the owning/admin account in Web Sessions, enable " &
                    "'Prompt for myrealm login', or use 'Sign in at next plugin reload'.")
            End If
            Return
        End If
        _nextAllowedUtc(evt.CharacterId) = DateTime.UtcNow + VerifyCooldown
        _expiryNotified = False

        ' Remember this realm_id — the session validator probes the
        ' realm's General/UpdateName page (which exists for the life
        ' of the realm, independent of any character lookup), so
        ' liveness checks work even before/without a successful name
        ' resolution and across Manager restarts.
        If Not String.Equals(context.GetConfigValue(RealmIdConfigKey), realmId, StringComparison.Ordinal) Then
            context.SetConfigValue(RealmIdConfigKey, realmId)
        End If

        ' Verification with no change writes nothing.
        If verifying AndAlso String.Equals(characterName, knownName, StringComparison.Ordinal) Then Return

        context.ContributeIdentity(New UtilityIdentityInfo With {
            .GameId = evt.GameId,
            .SessionScope = sessionScope,
            .CharacterId = evt.CharacterId,
            .Platform = evt.Platform,
            .PlatformUserId = evt.PlatformUserId,
            .CharacterName = characterName
        })
        If verifying Then
            context.LogInformation(
                $"myrealm rename detected for character {evt.CharacterId}: '{knownName}' → '{characterName}' (realm {realmId}).")
        Else
            context.LogInformation(
                $"myrealm resolved character {evt.CharacterId} → '{characterName}' (realm {realmId}).")
        End If
    End Function

    Public Function ShutdownAsync() As Task Implements IUtilityPlugin.ShutdownAsync
        If _http IsNot Nothing Then _http.Dispose()
        _http = Nothing
        Return Task.CompletedTask
    End Function

    ' ------------------------------------------------------------
    '  IWebSessionValidator — "does the session still work?"
    '  (7-5b; invoked from the Web Sessions tab, OUTSIDE the event
    '  queue — must be thread-safe and side-effect-light, so this
    '  never invalidates or notifies; it only classifies. The UI
    '  offers the revoke.)
    ' ------------------------------------------------------------

    Public Function CanValidateWebSession(sessionKey As String) As Boolean _
        Implements IWebSessionValidator.CanValidateWebSession
        Return sessionKey IsNot Nothing AndAlso
               sessionKey.StartsWith("myrealm:", StringComparison.OrdinalIgnoreCase)
    End Function

    Public Async Function ValidateWebSessionAsync(sessionKey As String,
                                                  cookieHeader As String,
                                                  context As IUtilityContext) As Task(Of WebSessionValidationResult) _
        Implements IWebSessionValidator.ValidateWebSessionAsync

        Dim realmId = context.GetConfigValue(RealmIdConfigKey)
        Dim client = _http
        If client Is Nothing Then
            Return New WebSessionValidationResult With {
                .State = WebSessionValidationState.Failed,
                .Detail = "Plugin is shutting down."}
        End If

        ' No realm learned from gameplay yet? Discover one from the
        ' portal itself — the authenticated landing page links every
        ' /customer/{id} the session can access, and each customer
        ' page links its /realm/{id}. (A slice of the 7-6 discovery
        ' design pulled forward so Validate works immediately after
        ' sign-in instead of gating on a join event.) Discovery
        ' doubles as validation: a redirect at the landing page IS
        ' the expired verdict.
        If String.IsNullOrEmpty(realmId) Then
            Dim disc = Await DiscoverRealmIdAsync(cookieHeader)
            If disc.FailureVerdict IsNot Nothing Then Return disc.FailureVerdict
            realmId = disc.RealmId
            If String.IsNullOrEmpty(realmId) Then
                ' The landing page served customer links (so the
                ' session is live — not a redirect/sign-in), but none
                ' of those customers has a realm configured yet. That
                ' is a VALID session, just nothing to probe; don't
                ' learn a realm_id and don't report failure.
                If disc.SawCustomers Then
                    Return New WebSessionValidationResult With {
                        .State = WebSessionValidationState.Valid,
                        .Detail = "signed in; no realm configured yet"}
                End If
                Return New WebSessionValidationResult With {
                    .State = WebSessionValidationState.Failed,
                    .Detail = "Signed in, but no customer/realm links found on the portal — capture the landing-page HTML if this persists."}
            End If
            context.SetConfigValue(RealmIdConfigKey, realmId)
        End If

        Dim probe = Await ProbeRealmPageAsync(realmId, cookieHeader)
        ' On a valid session, surface the realm name in the detail —
        ' the same value the realm-name autofill would use.
        If probe.Verdict.State = WebSessionValidationState.Valid AndAlso Not String.IsNullOrEmpty(probe.RealmName) Then
            probe.Verdict.Detail = $"realm ""{probe.RealmName}"" reachable"
        End If
        Return probe.Verdict
    End Function

    ''' <summary>Composite result for the realm-page probe — async
    ''' methods cannot have ByRef parameters (BC36926), so the realm
    ''' name rides alongside the verdict instead of an out-param.</summary>
    Private Class RealmProbeResult
        Public Property Verdict As WebSessionValidationResult
        Public Property RealmName As String
    End Class

    ''' <summary>Discovery outcome: RealmId on success; FailureVerdict
    ''' set when discovery itself reached a terminal verdict (expired
    ''' session, network failure). SawCustomers distinguishes "live
    ''' session, no realm configured yet" (customers present, no
    ''' realm) from "couldn't read the portal" (no customers at
    ''' all).</summary>
    Private Class RealmDiscoveryResult
        Public Property RealmId As String
        Public Property FailureVerdict As WebSessionValidationResult
        Public Property SawCustomers As Boolean
    End Class

    ''' <summary>Tiny GET helper for discovery (no auto-redirect, no
    ''' side effects). Body is Nothing unless HTTP 2xx.</summary>
    Private Class FetchedPage
        Public Property Status As Integer
        Public Property Redirected As Boolean
        Public Property Body As String
    End Class

    Private Async Function GetPageAsync(url As String, cookieHeader As String) As Task(Of FetchedPage)
        Dim page As New FetchedPage()
        Using request As New HttpRequestMessage(HttpMethod.Get, url)
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader)
            Using response = Await _http.SendAsync(request)
                page.Status = CInt(response.StatusCode)
                page.Redirected = page.Status >= 300 AndAlso page.Status < 400
                If response.IsSuccessStatusCode Then
                    page.Body = Await response.Content.ReadAsStringAsync()
                End If
            End Using
        End Using
        Return page
    End Function

    ''' <summary>Finds a realm the session can access, without any
    ''' gameplay: GET the authenticated landing page, harvest every
    ''' /customer/{id} link (owned realm + admin'd realms), then take
    ''' the first customer page that links a /realm/{id}. Used by the
    ''' validator when no realm_id has been learned from events yet;
    ''' it's also the seed of the 7-6 session→realm access map.</summary>
    Private Async Function DiscoverRealmIdAsync(cookieHeader As String) As Task(Of RealmDiscoveryResult)
        Dim result As New RealmDiscoveryResult()
        Try
            Dim landing = Await GetPageAsync(BaseUrl & "/", cookieHeader)
            If landing.Redirected Then
                result.FailureVerdict = New WebSessionValidationResult With {
                    .State = WebSessionValidationState.Expired,
                    .Detail = $"HTTP {landing.Status} redirect at the portal landing (signed out)"}
                Return result
            End If
            If landing.Body Is Nothing Then
                result.FailureVerdict = New WebSessionValidationResult With {
                    .State = WebSessionValidationState.Failed,
                    .Detail = $"HTTP {landing.Status} at the portal landing"}
                Return result
            End If

            Dim customerIds As New List(Of String)
            For Each m As Match In Regex.Matches(landing.Body, "(?i)/customer/(\d+)")
                Dim id = m.Groups(1).Value
                If Not customerIds.Contains(id) Then customerIds.Add(id)
            Next
            If customerIds.Count = 0 Then
                If landing.Body.IndexOf("SignIn", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   landing.Body.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    result.FailureVerdict = New WebSessionValidationResult With {
                        .State = WebSessionValidationState.Expired,
                        .Detail = "sign-in page served at the portal landing"}
                End If
                Return result
            End If
            result.SawCustomers = True

            ' First realm found wins — for liveness, ANY reachable
            ' realm proves the session works. Cap the walk defensively.
            Dim walked As Integer = 0
            For Each customerId In customerIds
                walked += 1
                If walked > 5 Then Exit For
                Dim page = Await GetPageAsync($"{BaseUrl}/customer/{customerId}", cookieHeader)
                If page.Body Is Nothing Then Continue For
                Dim realmMatch = Regex.Match(page.Body, "(?i)/realm/(\d+)")
                If realmMatch.Success Then
                    result.RealmId = realmMatch.Groups(1).Value
                    Return result
                End If
            Next
            Return result
        Catch ex As Exception
            result.FailureVerdict = New WebSessionValidationResult With {
                .State = WebSessionValidationState.Failed,
                .Detail = ex.Message}
            Return result
        End Try
    End Function

    ''' <summary>GETs the realm's General/UpdateName page with the
    ''' supplied cookie header and classifies the session. The realm
    ''' Name textbox value (the realm's display name) rides the
    ''' result on success — the page exists for the life of the
    ''' realm, so this is a more reliable liveness probe than the
    ''' per-character rename page. No auto-redirect: a 3xx or a
    ''' served sign-in page means the session expired.</summary>
    Private Async Function ProbeRealmPageAsync(realmId As String,
                                               cookieHeader As String) As Task(Of RealmProbeResult)
        Dim result As New RealmProbeResult()
        Dim url = $"{BaseUrl}/realm/{realmId}/General/UpdateName"
        Try
            Using request As New HttpRequestMessage(HttpMethod.Get, url)
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader)
                Using response = Await _http.SendAsync(request)
                    Dim status = CInt(response.StatusCode)
                    If status >= 300 AndAlso status < 400 Then
                        result.Verdict = New WebSessionValidationResult With {
                            .State = WebSessionValidationState.Expired,
                            .Detail = $"HTTP {status} redirect (signed out)"}
                        Return result
                    End If
                    If Not response.IsSuccessStatusCode Then
                        result.Verdict = New WebSessionValidationResult With {
                            .State = WebSessionValidationState.Failed,
                            .Detail = $"HTTP {status} at the realm page"}
                        Return result
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync()
                    result.RealmName = ExtractInputValue(body, "Name")
                    If Not String.IsNullOrEmpty(result.RealmName) Then
                        result.Verdict = New WebSessionValidationResult With {
                            .State = WebSessionValidationState.Valid,
                            .Detail = "HTTP 200, realm page served"}
                        Return result
                    End If
                    If body.IndexOf("SignIn", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                       body.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0 Then
                        result.Verdict = New WebSessionValidationResult With {
                            .State = WebSessionValidationState.Expired,
                            .Detail = "sign-in page served"}
                        Return result
                    End If
                    result.Verdict = New WebSessionValidationResult With {
                        .State = WebSessionValidationState.Failed,
                        .Detail = $"HTTP 200 but no realm Name field ({body.Length} chars)"}
                    Return result
                End Using
            End Using
        Catch ex As Exception
            result.Verdict = New WebSessionValidationResult With {
                .State = WebSessionValidationState.Failed,
                .Detail = ex.Message}
            Return result
        End Try
        Return result
    End Function

    ''' <summary>Reads the realm's current display name from the
    ''' General/UpdateName page using a known-good session. Returns
    ''' Nothing if the session is invalid or the page can't be read.
    ''' Exposed for the realm-name autofill (Phase 7-6 LO realm
    ''' config); validation uses it internally too.</summary>
    Friend Async Function TryReadRealmNameAsync(realmId As String, cookieHeader As String) As Task(Of String)
        Dim probe = Await ProbeRealmPageAsync(realmId, cookieHeader)
        If probe.Verdict.State = WebSessionValidationState.Valid Then Return probe.RealmName
        Return Nothing
    End Function

    ' ------------------------------------------------------------
    '  IWebPortalDataProvider — portal onboarding discovery (7-6)
    '
    '  Generalises the validator's first-realm DiscoverRealmIdAsync
    '  into a full READ-ONLY harvest: every /customer/{id} the
    '  session can reach (owned + admin'd), each customer's realm
    '  (/customer/{id} -> /realm/{id}), its realm name (the
    '  General/UpdateName Name input) and its customer + provider
    '  keys (the /customer/{id}/Providers page). Emits ONE
    '  WebPortalImportRecord per (customer key, provider key) pair:
    '  a realm hosted from several providers becomes several records
    '  sharing a RealmName but differing by ProviderKey.
    '
    '  GETs only; no writes back to myrealm. Driven OUTSIDE the
    '  event queue (and self-invoked after a manual sign-in for a
    '  discovery log dump in 7-6 slice 1, before the Manager-side
    '  import UI exists).
    ' ------------------------------------------------------------

    Public Function CanProvideForSession(sessionKey As String) As Boolean _
        Implements IWebPortalDataProvider.CanProvideForSession
        Return sessionKey IsNot Nothing AndAlso
               sessionKey.StartsWith("myrealm:", StringComparison.OrdinalIgnoreCase)
    End Function

    Public Async Function DiscoverRecordsAsync(requestedKey As String,
                                               allowPrompt As Boolean,
                                               context As IUtilityContext) As Task(Of IReadOnlyList(Of WebPortalImportRecord)) _
        Implements IWebPortalDataProvider.DiscoverRecordsAsync

        ' Phase 7-7: discover across EVERY signed-in myrealm account, not
        ' just the default. Enumerate this plugin's stored "myrealm:*"
        ' sessions, scrape each read-only with its own header, aggregate,
        ' and dedup by (CustomerKey, ProviderKey) — owner + admins of the
        ' same realm scrape the same pair and collapse to one record
        ' (decision 3). Per-account fetch never prompts (allowPrompt:=
        ' False); the only login prompt is the zero-accounts case below.
        Dim records As New List(Of WebPortalImportRecord)
        If _http Is Nothing Then Return records

        Dim keys = EnumerateMyrealmKeys(context, requestedKey)

        ' No accounts stored yet + prompting allowed (the Import path):
        ' capture a first login, then scrape it. Only for the default
        ' onboarding case (requestedKey = Nothing); a concrete-but-unstored
        ' key just yields nothing.
        If keys.Count = 0 Then
            If allowPrompt AndAlso String.IsNullOrEmpty(requestedKey) Then
                Dim newName = Await CaptureAndStoreAccountAsync(context)
                If String.IsNullOrEmpty(newName) Then Return records
                keys.Add("myrealm:" & newName)
            Else
                Return records
            End If
        End If

        ' Scrape each account with its own session; first occurrence of a
        ' (CustomerKey, ProviderKey) pair wins (Ordinal — keys are
        ' case-sensitive, matching the Manager's upsert comparison).
        Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
        For Each accountKey In keys
            Dim cookieHeader = Await context.GetOrCaptureWebSessionAsync(
                accountKey, BaseUrl & "/", "/customer/", "myrealm.lastoasis.gg", False)
            If String.IsNullOrEmpty(cookieHeader) Then
                context.LogInformation("Portal discovery: no stored session for '" & accountKey & "'; skipped.")
                Continue For
            End If

            For Each rec In Await ScrapeAccountAsync(accountKey, cookieHeader, context)
                Dim dedupKey = If(rec.Fields("CustomerKey"), "") & "|" & If(rec.Fields("ProviderKey"), "")
                If seen.Add(dedupKey) Then records.Add(rec)
            Next
        Next
        Return records
    End Function

    ''' <summary>The account keys to scrape: a concrete requestedKey
    ''' (scoped to that one myrealm account) when the host passes one,
    ''' else EVERY "myrealm:*" session this plugin has stored (the
    ''' default + each added account). Static + locals only — no
    ''' parameter named like the SessionKey constant (the 7-6 shadowing
    ''' bug).</summary>
    Private Shared Function EnumerateMyrealmKeys(context As IUtilityContext,
                                                 requestedKey As String) As List(Of String)
        Dim keys As New List(Of String)
        If Not String.IsNullOrEmpty(requestedKey) Then
            If requestedKey.StartsWith("myrealm:", StringComparison.OrdinalIgnoreCase) Then keys.Add(requestedKey)
            Return keys
        End If
        For Each summary In context.ListWebSessions()
            If summary?.SessionKey IsNot Nothing AndAlso
               summary.SessionKey.StartsWith("myrealm:", StringComparison.OrdinalIgnoreCase) Then
                keys.Add(summary.SessionKey)
            End If
        Next
        Return keys
    End Function

    ''' <summary>Read-only scrape of ONE account's portal with its own
    ''' cookie header: landing -> every /customer/{id} -> each customer's
    ''' realm -> its customer + provider keys -> one record per provider
    ''' key. A redirected/unreadable landing means this account's session
    ''' is stale; since the caller passed allowPrompt:=False it is skipped,
    ''' not re-prompted (the operator refreshes accounts from the Web
    ''' Sessions form). Never throws.</summary>
    Private Async Function ScrapeAccountAsync(accountKey As String,
                                              cookieHeader As String,
                                              context As IUtilityContext) As Task(Of List(Of WebPortalImportRecord))
        Dim records As New List(Of WebPortalImportRecord)
        Try
            ' Landing page -> every /customer/{id} the session reaches.
            Dim landing = Await GetPageAsync(BaseUrl & "/", cookieHeader)
            If landing.Redirected OrElse landing.Body Is Nothing Then
                Dim why = If(landing.Redirected, " redirect - signed out", "")
                context.LogWarning(
                    "Portal discovery: '" & accountKey & "' landing not readable (HTTP " & landing.Status & why & "); skipped.")
                Return records
            End If

            Dim customerIds As New List(Of String)
            For Each m As Match In Regex.Matches(landing.Body, "(?i)/customer/(\d+)")
                Dim id = m.Groups(1).Value
                If Not customerIds.Contains(id) Then customerIds.Add(id)
            Next
            If customerIds.Count = 0 Then
                context.LogInformation("Portal discovery: '" & accountKey & "' signed in, but listed no customers; nothing to import.")
                Return records
            End If

            For Each customerId In customerIds
                ' Customer dashboard -> realm_id (0 or 1). No realm =
                ' valid but nothing to onboard yet; skip + note.
                Dim customerPage = Await GetPageAsync(BaseUrl & "/customer/" & customerId, cookieHeader)
                If customerPage.Body Is Nothing Then
                    context.LogWarning("Portal discovery: customer " & customerId & " page not readable (HTTP " & customerPage.Status & "); skipped.")
                    Continue For
                End If
                Dim realmMatch = Regex.Match(customerPage.Body, "(?i)/realm/(\d+)")
                If Not realmMatch.Success Then
                    context.LogInformation("Portal discovery: customer " & customerId & " has no realm configured yet; skipped.")
                    Continue For
                End If
                Dim realmId = realmMatch.Groups(1).Value

                ' Providers page -> customer key + provider keys.
                Dim providers = Await ScrapeProvidersAsync(customerId, cookieHeader)
                If String.IsNullOrEmpty(providers.CustomerKey) OrElse providers.Entries.Count = 0 Then
                    context.LogWarning(
                        "Portal discovery: customer " & customerId & " (realm " & realmId &
                        ") - could not read a customer key and/or any provider keys from the Providers page; skipped.")
                    Continue For
                End If

                ' Realm name (cosmetic; blank tolerated).
                Dim realmName = Await TryReadRealmNameAsync(realmId, cookieHeader)
                Dim realmNameVal = If(realmName, "")

                ' One record per provider key.
                For Each entry In providers.Entries
                    ' Group display name: bare RealmName, suffixed with
                    ' the provider label only when it adds information
                    ' (so per-provider groups stay distinguishable in
                    ' pickers; History reads the RealmName field). Blank
                    ' realm name falls back to the label or realm id.
                    Dim display As String
                    If String.IsNullOrEmpty(realmNameVal) Then
                        display = If(Not String.IsNullOrEmpty(entry.UsedBy), entry.UsedBy, "realm " & realmId)
                    ElseIf Not String.IsNullOrEmpty(entry.UsedBy) AndAlso
                           Not String.Equals(entry.UsedBy, realmNameVal, StringComparison.OrdinalIgnoreCase) Then
                        display = realmNameVal & " (" & entry.UsedBy & ")"
                    Else
                        display = realmNameVal
                    End If

                    Dim record As New WebPortalImportRecord With {
                        .GameId = "lastoasis",
                        .SharedConfigKey = "realm",
                        .UsedBy = entry.UsedBy,
                        .SourceRef = realmId,
                        .SuggestedDisplayName = display,
                        .MatchFieldKeys = New List(Of String) From {"CustomerKey", "ProviderKey"}
                    }
                    record.Fields("CustomerKey") = providers.CustomerKey
                    record.Fields("ProviderKey") = entry.Key
                    record.Fields("RealmName") = realmNameVal
                    records.Add(record)
                Next
            Next
        Catch ex As Exception
            context.LogWarning("Portal discovery for '" & accountKey & "' failed: " & ex.Message)
        End Try
        Return records
    End Function

    ''' <summary>Add-account flow (Phase 7-7): force a FRESH interactive
    ''' login (CaptureWebSessionAsync always opens the dialog, even when
    ''' sessions already exist), derive this account's key, store it, and
    ''' return the account label. Nothing on cancel/failure. Distinct
    ''' from discovery's first-capture, which reuses any stored session —
    ''' this is how the operator adds ANOTHER account.</summary>
    Public Async Function AddAccountAsync(context As IUtilityContext) As Task(Of String) _
        Implements IWebPortalDataProvider.AddAccountAsync
        Return Await CaptureAndStoreAccountAsync(context)
    End Function

    ''' <summary>Capture a fresh login, derive the account key, store the
    ''' session, return the account name (label). Shared by AddAccountAsync
    ''' and (slice 4) discovery's zero-accounts first capture. Key is
    ''' "myrealm:{name}" from the landing greeting; when the greeting
    ''' can't be read it falls back to "myrealm:customer-{id}" off the
    ''' completion URL so two distinct accounts never collide. Never
    ''' throws.</summary>
    Private Async Function CaptureAndStoreAccountAsync(context As IUtilityContext) As Task(Of String)
        Try
            Dim result = Await context.CaptureWebSessionAsync(
                BaseUrl & "/", "/customer/", "myrealm.lastoasis.gg")
            If result Is Nothing OrElse Not result.Ok Then Return Nothing

            Dim header = BuildCookieHeader(result.Cookies)
            If String.IsNullOrEmpty(header) Then
                context.LogWarning("Add myrealm account: capture completed but yielded no cookies.")
                Return Nothing
            End If

            ' Prefer the landing greeting ("Hello {name}!"); fall back to
            ' the completion URL's customer id so distinct accounts never
            ' share a key.
            Dim name = Await ReadAccountNameAsync(header)
            If String.IsNullOrEmpty(name) Then
                Dim custMatch = Regex.Match(If(result.CompletionUrl, ""), "(?i)/customer/(\d+)")
                If custMatch.Success Then
                    name = "customer-" & custMatch.Groups(1).Value
                    context.LogInformation(
                        "Add myrealm account: landing greeting not readable; keyed by customer id (" & name & ").")
                Else
                    context.LogWarning(
                        "Add myrealm account: could not read an account name or customer id; " &
                        "not storing, to avoid colliding two accounts under one key. Try again.")
                    Return Nothing
                End If
            End If

            Dim key = "myrealm:" & name
            context.StoreWebSession(key, header)
            context.LogInformation("Add myrealm account: stored session '" & key & "'.")
            Return name
        Catch ex As Exception
            context.LogWarning("Add myrealm account failed: " & ex.Message)
            Return Nothing
        End Try
    End Function

    ''' <summary>Reads the account name from the authenticated landing
    ''' page, which greets "Hello {name}!" (e.g. "site_ml"). Tags between
    ''' the greeting and the name are tolerated (flattened via
    ''' CleanCellText). Returns Nothing when the greeting can't be read
    ''' (markup change, odd account, or a signed-out response) — the
    ''' caller then falls back to a unique key. Read-only GET.</summary>
    Private Async Function ReadAccountNameAsync(cookieHeader As String) As Task(Of String)
        Try
            Dim landing = Await GetPageAsync(BaseUrl & "/", cookieHeader)
            If landing.Body Is Nothing Then Return Nothing
            Dim m = Regex.Match(landing.Body, "(?is)Hello\s+(.{1,80}?)!")
            If Not m.Success Then Return Nothing
            Dim name = CleanCellText(m.Groups(1).Value)
            Return If(String.IsNullOrEmpty(name), Nothing, name)
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>Builds a "name=value; ..." Cookie header from the
    ''' captured cookie list (CaptureWebSessionAsync returns cookies, not
    ''' a header string). Mirrors the store's own builder.</summary>
    Private Shared Function BuildCookieHeader(cookies As List(Of CapturedCookie)) As String
        If cookies Is Nothing Then Return Nothing
        Dim parts As New List(Of String)
        For Each c In cookies
            If c Is Nothing OrElse String.IsNullOrEmpty(c.Name) Then Continue For
            parts.Add(c.Name & "=" & If(c.Value, ""))
        Next
        If parts.Count = 0 Then Return Nothing
        Return String.Join("; ", parts)
    End Function

    ''' <summary>Customer-scoped scrape of /customer/{id}/Providers:
    ''' the readonly "Key" input is the customer key; each table row
    ''' carries a provider key (off the .../Providers/{key}/delete
    ''' href) and a "Used by" label. Best-effort - a missing label
    ''' leaves UsedBy Nothing; the key list is anchored on the delete
    ''' href so it survives attribute/column reordering.</summary>
    Private Async Function ScrapeProvidersAsync(customerId As String,
                                                cookieHeader As String) As Task(Of ProvidersInfo)
        Dim info As New ProvidersInfo()
        Dim page = Await GetPageAsync(BaseUrl & "/customer/" & customerId & "/Providers", cookieHeader)
        If page.Body Is Nothing Then Return info

        info.CustomerKey = ExtractInputValue(page.Body, "Key")

        ' Pair each provider key with its row's "Used by" label.
        ' Row-scoped so the label binds to the right key; the key
        ' itself comes off the delete href (attribute-order-proof).
        For Each rowMatch As Match In Regex.Matches(page.Body, "(?is)<tr\b[^>]*>(.*?)</tr>")
            Dim row = rowMatch.Groups(1).Value
            Dim keyMatch = Regex.Match(row, "(?i)/Providers/([A-Za-z0-9]+)/delete")
            If Not keyMatch.Success Then Continue For
            Dim entry As New ProviderEntry With {.Key = keyMatch.Groups(1).Value}

            ' "Used by" = the row cell that is neither the key cell nor
            ' the actions cell. Strip tags, take the first non-empty
            ' text; tolerate the empty <div> the page emits after it.
            For Each cell As Match In Regex.Matches(row, "(?is)<td\b[^>]*>(.*?)</td>")
                Dim txt = CleanCellText(cell.Groups(1).Value)
                If txt.Length = 0 Then Continue For
                If String.Equals(txt, entry.Key, StringComparison.Ordinal) Then Continue For
                If txt.IndexOf("Remove", StringComparison.OrdinalIgnoreCase) >= 0 Then Continue For
                entry.UsedBy = txt
                Exit For
            Next
            info.Entries.Add(entry)
        Next
        Return info
    End Function

    ''' <summary>Flatten an HTML table cell's inner markup to a single
    ''' text line: drop tags, decode entities, collapse whitespace.</summary>
    Private Shared Function CleanCellText(cellHtml As String) As String
        If String.IsNullOrEmpty(cellHtml) Then Return ""
        Dim noTags = Regex.Replace(cellHtml, "(?s)<.*?>", " ")
        Dim decoded = WebUtility.HtmlDecode(noTags)
        Return Regex.Replace(decoded, "\s+", " ").Trim()
    End Function

    ''' <summary>One provider key + its "Used by" label.</summary>
    Private Class ProviderEntry
        Public Property Key As String
        Public Property UsedBy As String
    End Class

    ''' <summary>Customer key + the provider entries scraped from one
    ''' /customer/{id}/Providers page.</summary>
    Private Class ProvidersInfo
        Public Property CustomerKey As String
        Public Property Entries As New List(Of ProviderEntry)
    End Class

    ' ------------------------------------------------------------
    '  Fetch + parse
    ' ------------------------------------------------------------

    ''' <summary>Resolves a character's name for a realm. Tries the
    ''' session that served this realm last (the failover cache), then
    ''' every other stored myrealm:* account, using the first whose
    ''' rename page yields a Name; caches the winner (realmId → key) and
    ''' drops the entry when none works. A redirect / sign-in / 403 /
    ''' no-Name response just means "this account can't serve this realm"
    ''' (expired OR not its realm) — we move on without invalidating
    ''' anyone (decision 5; revoke stays a UI action). allowPromptIfNo-
    ''' Accounts opens ONE host-throttled login only when nothing is
    ''' stored at all. Returns Nothing when no account can serve the
    ''' realm. Never throws.</summary>
    Private Async Function ResolveNameWithFailoverAsync(realmId As String,
                                                        characterId As String,
                                                        allowPromptIfNoAccounts As Boolean,
                                                        context As IUtilityContext) As Task(Of String)
        Dim url = $"{BaseUrl}/realm/{realmId}/Characters/{characterId}/Rename"

        ' Fast path: the session that served this realm last time.
        Dim preferred As String = Nothing
        SyncLock _realmSessionGate
            _realmSession.TryGetValue(realmId, preferred)
        End SyncLock
        If Not String.IsNullOrEmpty(preferred) Then
            Dim hit = Await TryReadNameWithKeyAsync(preferred, url, context)
            If Not String.IsNullOrEmpty(hit) Then Return hit
        End If

        ' Failover: every other stored myrealm:* account; first that
        ' reaches the realm wins and is cached.
        Dim tried As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If Not String.IsNullOrEmpty(preferred) Then tried.Add(preferred)
        Dim anyAccount = False
        For Each summary In context.ListWebSessions()
            Dim k = summary?.SessionKey
            If String.IsNullOrEmpty(k) OrElse
               Not k.StartsWith("myrealm:", StringComparison.OrdinalIgnoreCase) Then Continue For
            anyAccount = True
            If Not tried.Add(k) Then Continue For
            Dim name = Await TryReadNameWithKeyAsync(k, url, context)
            If Not String.IsNullOrEmpty(name) Then
                SyncLock _realmSessionGate
                    _realmSession(realmId) = k
                End SyncLock
                If String.IsNullOrEmpty(preferred) Then
                    context.LogInformation("myrealm: realm " & realmId & " served by '" & k & "'.")
                Else
                    context.LogInformation("myrealm: realm " & realmId & " failed over from '" & preferred & "' to '" & k & "'.")
                End If
                Return name
            End If
        Next

        ' No account stored at all -> optional first-time prompt. Use the
        ' host-THROTTLED GetOrCapture (<=1 dialog/run, in-flight dedup),
        ' not the raw CaptureWebSessionAsync, since this fires on every
        ' un-named join. Capture is keyless, so derive the name and re-home
        ' the transient default -> myrealm:{name} (fall back to default
        ' only if the greeting can't be read).
        If Not anyAccount AndAlso allowPromptIfNoAccounts Then
            Try
                Dim header = Await context.GetOrCaptureWebSessionAsync(
                    SessionKey, BaseUrl & "/", "/customer/", "myrealm.lastoasis.gg", True)
                If Not String.IsNullOrEmpty(header) Then
                    Dim useKey = SessionKey
                    Dim derived = Await ReadAccountNameAsync(header)
                    If Not String.IsNullOrEmpty(derived) Then
                        useKey = "myrealm:" & derived
                        context.StoreWebSession(useKey, header)
                        context.InvalidateWebSession(SessionKey)
                    End If
                    Dim renamePage = Await GetPageAsync(url, header)
                    Dim name = If(renamePage.Body Is Nothing, Nothing, ExtractInputValue(renamePage.Body, "Name"))
                    If Not String.IsNullOrEmpty(name) Then
                        SyncLock _realmSessionGate
                            _realmSession(realmId) = useKey
                        End SyncLock
                        context.LogInformation("myrealm: realm " & realmId & " served by '" & useKey & "' (first login).")
                        Return name
                    End If
                End If
            Catch ex As Exception
                context.LogWarning("myrealm first-login capture failed: " & ex.Message)
            End Try
        End If

        ' Nobody served it — drop any stale preference for this realm.
        SyncLock _realmSessionGate
            _realmSession.Remove(realmId)
        End SyncLock
        Return Nothing
    End Function

    ''' <summary>One account: fetch the rename page (read-only, no auto-
    ''' redirect via GetPageAsync) and return its Name value, or Nothing
    ''' if the session has no live header, the page didn't 200, or there
    ''' was no Name input (redirect / sign-in / not this account's realm).
    ''' No side effects — classification is the caller's job (try next).
    ''' Swallows network errors (logged) so one flaky account doesn't
    ''' abort the walk.</summary>
    Private Async Function TryReadNameWithKeyAsync(key As String, url As String,
                                                   context As IUtilityContext) As Task(Of String)
        Try
            Dim header = Await context.GetOrCaptureWebSessionAsync(
                key, BaseUrl & "/", "/customer/", "myrealm.lastoasis.gg", False)
            If String.IsNullOrEmpty(header) Then Return Nothing
            Dim page = Await GetPageAsync(url, header)
            If page.Body Is Nothing Then Return Nothing
            Return ExtractInputValue(page.Body, "Name")
        Catch ex As Exception
            context.LogWarning("myrealm lookup via '" & key & "' failed: " & ex.Message)
            Return Nothing
        End Try
    End Function

    ''' <summary>Attribute-order-tolerant extraction of a named
    ''' &lt;input&gt;'s value: find an &lt;input&gt; tag whose id or
    ''' name equals fieldName, then pull its value attribute.
    ''' HTML-decoded. Used for the realm "Name" input and the
    ''' Providers page's customer-"Key" input.</summary>
    Friend Shared Function ExtractInputValue(html As String, fieldName As String) As String
        If String.IsNullOrEmpty(html) Then Return Nothing
        Dim tagPattern = "(?is)<input\b[^>]*?\b(?:id|name)\s*=\s*[""']" & Regex.Escape(fieldName) & "[""'][^>]*?>"
        For Each m As Match In Regex.Matches(html, tagPattern)
            Dim valueMatch = Regex.Match(m.Value, "(?is)\bvalue\s*=\s*[""']([^""']*)[""']")
            If valueMatch.Success Then
                Dim value = WebUtility.HtmlDecode(valueMatch.Groups(1).Value).Trim()
                If value.Length > 0 Then Return value
            End If
        Next
        Return Nothing
    End Function

    ' ------------------------------------------------------------
    '  Helpers
    ' ------------------------------------------------------------

    ''' <summary>Parses "lastoasis:{realm_id}:{tile_id}" into the full
    ''' within-game scope (everything after the first colon — the
    ''' resolver's identity-universe key) and the numeric realm_id.
    ''' False for fallback identities.</summary>
    Friend Shared Function TryParseRealm(sessionIdentity As String,
                                         ByRef sessionScope As String,
                                         ByRef realmId As String) As Boolean
        sessionScope = Nothing
        realmId = Nothing
        If String.IsNullOrEmpty(sessionIdentity) Then Return False
        Dim firstColon = sessionIdentity.IndexOf(":"c)
        If firstColon < 0 OrElse firstColon = sessionIdentity.Length - 1 Then Return False
        sessionScope = sessionIdentity.Substring(firstColon + 1)

        Dim nextColon = sessionScope.IndexOf(":"c)
        Dim candidate = If(nextColon >= 0, sessionScope.Substring(0, nextColon), sessionScope)
        If candidate.Length = 0 Then Return False
        For Each ch In candidate
            If Not Char.IsDigit(ch) Then Return False
        Next
        realmId = candidate
        Return True
    End Function

    ''' <summary>Reads a boolean config value, defaulting to True when
    ''' unset/unparsable — for the always-on schema defaults.</summary>
    Private Shared Function IsEnabled(context As IUtilityContext, key As String) As Boolean
        Dim raw = context.GetConfigValue(key)
        If String.IsNullOrEmpty(raw) Then Return True
        Dim parsed As Boolean
        If Boolean.TryParse(raw, parsed) Then Return parsed
        Return True
    End Function

    ''' <summary>Reads a boolean config value, defaulting to FALSE when
    ''' unset — for opt-in flags like SignInOnNextReload.</summary>
    Private Shared Function IsFlagSet(context As IUtilityContext, key As String) As Boolean
        Dim parsed As Boolean
        Return Boolean.TryParse(context.GetConfigValue(key), parsed) AndAlso parsed
    End Function

End Class
