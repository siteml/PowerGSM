Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports GSM.Plugin

' ============================================================
'  Utility plugin contracts — Phase 7 (ContractsVersion 2)
'
'  A second plugin kind on the Manager: utility plugins aren't
'  tied to a game and don't manage installations or instances.
'  They react to Manager-wide events (player join/leave, chat,
'  server state, instance lifecycle) and act through a gated
'  context (IUtilityContext).
'
'  Utility plugins ride the same Phase 6 pipeline as game
'  plugins (.vb source in Plugins\, inline <plugin> manifest,
'  Roslyn compile, hot-reload) with two extra rules enforced by
'  PluginRegistry:
'    - a <plugin> manifest with id + version is REQUIRED (no
'      legacy/manifest-less leniency — utility plugins are new)
'    - IUtilityPlugin.PluginId must match the manifest id
'
'  Utility plugins must declare requiresContracts="2" in their
'  manifest — this surface first shipped in contracts v2, so a
'  v1 Manager fails them fast with one clear message instead of
'  a Roslyn "type not defined" cascade.
'
'  IMPORTANT (capability model honesty): the `requires`
'  capability list on the manifest is an informed-consent and
'  convenience-API mechanism, NOT a sandbox. Plugins are
'  full-trust compiled code. The context only hands out service
'  access for declared capabilities, and the install consent
'  prompt displays them — but the real defenses are source
'  provenance, readable .vb source, and never-auto-install.
'
'  GSM.Contracts has zero NuGet dependencies by design, so the
'  context exposes plain logging methods rather than ILogger.
' ============================================================

Namespace GSM.Utility

    ''' <summary>Event kinds delivered to utility plugins. New kinds
    ''' may be added over time (additive — does not bump contracts);
    ''' plugins switch on Kind and ignore what they don't know.</summary>
    Public Enum UtilityEventKind
        PlayerJoin
        PlayerLeave
        ChatMessage
        ServerStateChange
        InstanceStarted
        InstanceStopped
        InstanceCrashed
    End Enum

    ''' <summary>
    ''' One Manager-side event, delivered to subscribed utility
    ''' plugins. Only the fields relevant to the Kind are populated;
    ''' the rest are Nothing.
    ''' </summary>
    Public Class UtilityEvent
        Public Property Kind As UtilityEventKind
        Public Property TimestampUtc As DateTime

        ' Where it happened.
        Public Property NodeId As String
        Public Property InstallationId As String
        Public Property InstanceId As String
        Public Property InstanceDisplayName As String
        Public Property GameId As String

        ''' <summary>The Manager-side session identity this event was
        ''' attributed to — e.g. "lastoasis:{realm_id}:{tile_id}" on
        ''' Last Oasis, or the "{gameId}:{instanceId}" fallback for
        ''' games without a cross-instance session model. Plugins
        ''' needing game-specific components (like LO's realm_id)
        ''' parse them out of this string.</summary>
        Public Property SessionIdentity As String

        ' Player events / chat. PlayerName is the RAW name the
        ' parser observed (platform persona on LO/Conan, the
        ' multiplayer username on Factorio); CharacterName is the
        ' resolved in-game character name.
        Public Property PlayerName As String
        Public Property Platform As String
        Public Property PlatformUserId As String
        Public Property CharacterId As String

        ''' <summary>Resolved in-game character name — the identity
        ''' resolution's current best answer at dispatch time.
        ''' Nothing when unresolved (the gap a name-resolution
        ''' plugin exists to fill).</summary>
        Public Property CharacterName As String

        ' ChatMessage text / ServerStateChange detail (tile name).
        Public Property Message As String

        ' ServerStateChange / instance lifecycle.
        Public Property ServerState As String
    End Class

    ''' <summary>Identity record exchanged with the Manager's
    ''' identity resolution (capabilities identity-read /
    ''' identity-write). GameId is REQUIRED when contributing
    ''' (identities live per game); SessionScope further isolates
    ''' within a game (e.g. LO realm) — empty means game-wide.</summary>
    Public Class UtilityIdentityInfo
        Public Property GameId As String
        Public Property SessionScope As String
        Public Property CharacterId As String
        Public Property Platform As String
        Public Property PlatformUserId As String
        Public Property CharacterName As String
    End Class

    ''' <summary>Read-only instance summary (always available from
    ''' the context — the same data any Manager UI shows).</summary>
    Public Class UtilityInstanceInfo
        Public Property InstanceId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property NodeId As String
        Public Property State As String
    End Class

    ''' <summary>One cookie harvested by the web-session capture.</summary>
    Public Class CapturedCookie
        Public Property Name As String
        Public Property Value As String
        Public Property Domain As String
        Public Property Path As String
        Public Property ExpiresUtc As DateTime?
        Public Property IsHttpOnly As Boolean
        Public Property IsSecure As Boolean
    End Class

    ''' <summary>Result of a web-session capture (capability
    ''' web-capture). Not Ok when the user cancelled the dialog, the
    ''' WebView2 runtime is missing, or the capture failed.</summary>
    Public Class WebSessionCaptureResult
        Public Property Ok As Boolean
        Public Property ErrorMessage As String
        Public Property Cookies As New List(Of CapturedCookie)

        ''' <summary>The URL that matched completionUrlPattern — i.e.
        ''' the page the login landed on. Populated on a successful
        ''' capture; Nothing on cancel/failure. For portals whose
        ''' completion lands on an identity-bearing page (e.g.
        ''' myrealm's "/customer/{id}"), this hands the Manager that
        ''' id at sign-in time without a probe. Manager-side only —
        ''' the shared-session store (GetOrCaptureWebSessionAsync)
        ''' returns just the cookie header, so this never reaches the
        ''' plugin (by design — the plugin tracks its own realm via
        ''' config). Added Phase 7-6 decision 1; additive, no
        ''' ContractsVersion bump.</summary>
        Public Property CompletionUrl As String
    End Class

    ''' <summary>
    ''' Plain contracts-side summary of one stored web session, for
    ''' plugins that enumerate their own accounts (Phase 7-7 multi-
    ''' session). Key + timestamps only — never the cookie header.
    ''' The Manager-side WebSessionInfo can't cross into contracts, so
    ''' this is its minimal projection. Additive; no ContractsVersion
    ''' bump.
    ''' </summary>
    Public Class WebSessionSummary
        Public Property SessionKey As String
        Public Property CapturedAtUtc As DateTime
        Public Property LastUsedUtc As DateTime?
    End Class

    ''' <summary>
    ''' Capability names a utility plugin may declare in its
    ''' manifest's `requires` attribute (comma-separated). Unknown
    ''' names are a load warning, not an error (forward-compat).
    ''' </summary>
    Public Module UtilityCapabilities
        ''' <summary>Receive the event stream (HandleEventAsync).</summary>
        Public Const Events As String = "events"
        ''' <summary>Look up identities via the context.</summary>
        Public Const IdentityRead As String = "identity-read"
        ''' <summary>Contribute identities via the context.</summary>
        Public Const IdentityWrite As String = "identity-write"
        ''' <summary>Send notifications through the Manager's
        ''' notification plugins.</summary>
        Public Const Notifications As String = "notifications"
        ''' <summary>Informational only: the plugin makes its own
        ''' outbound network calls. Displayed at install consent;
        ''' nothing to gate (the plugin owns its HttpClient).</summary>
        Public Const Network As String = "network"
        ''' <summary>Persist per-plugin config values via the context.</summary>
        Public Const Config As String = "config"
        ''' <summary>May request an embedded-browser login (the user
        ''' authenticates in a Manager-rendered WebView2 dialog) and
        ''' receives the resulting session cookies.</summary>
        Public Const WebCapture As String = "web-capture"

        ''' <summary>Parse a manifest `requires` attribute into a
        ''' normalised (trimmed, lower-cased, de-duplicated) list.
        ''' Never throws; Nothing/empty yields an empty list.</summary>
        Public Function ParseList(requiresAttribute As String) As List(Of String)
            Dim result As New List(Of String)
            If String.IsNullOrWhiteSpace(requiresAttribute) Then Return result
            For Each part In requiresAttribute.Split(","c)
                Dim name = part.Trim().ToLowerInvariant()
                If name.Length > 0 AndAlso Not result.Contains(name) Then
                    result.Add(name)
                End If
            Next
            Return result
        End Function

        ''' <summary>True when the name is a capability this contracts
        ''' version knows. Unknown names are a load WARNING, not an
        ''' error (forward-compat with future capability sets).</summary>
        Public Function IsKnown(name As String) As Boolean
            Select Case If(name, "").Trim().ToLowerInvariant()
                Case Events, IdentityRead, IdentityWrite, Notifications, Network, Config, WebCapture
                    Return True
                Case Else
                    Return False
            End Select
        End Function
    End Module

    ''' <summary>
    ''' The gated service surface handed to utility plugins. Calls
    ''' for undeclared capabilities throw InvalidOperationException
    ''' with a message naming the missing capability. Logging and the
    ''' read-only instance listing are always available.
    ''' </summary>
    Public Interface IUtilityContext

        ' --- Always available ---
        Sub LogInformation(message As String)
        Sub LogWarning(message As String)
        Sub LogError(message As String)
        Function GetInstances() As IReadOnlyList(Of UtilityInstanceInfo)

        ' --- notifications ---
        Function SendNotificationAsync(title As String, message As String) As Task(Of Boolean)

        ' --- identity-read / identity-write ---
        Function ResolveIdentity(characterId As String) As UtilityIdentityInfo
        Sub ContributeIdentity(identity As UtilityIdentityInfo)

        ' --- config ---
        Function GetConfigValue(key As String) As String
        Sub SetConfigValue(key As String, value As String)

        ' --- web-capture ---
        ''' <summary>
        ''' Show a Manager-owned embedded-browser dialog at startUrl,
        ''' let the user perform a real login, and harvest the cookies
        ''' for cookieDomain once navigation reaches a URL containing
        ''' completionUrlPattern. Returns a not-Ok result on cancel,
        ''' missing WebView2 runtime, or failure. Safe to call from
        ''' any thread (the Manager marshals to its UI thread).
        ''' </summary>
        Function CaptureWebSessionAsync(startUrl As String,
                                        completionUrlPattern As String,
                                        cookieDomain As String) As Task(Of WebSessionCaptureResult)

        ' --- web-capture: shared session store (Phase 7-5) ---
        ''' <summary>
        ''' Return the cookie header of the named shared web session,
        ''' capturing it first via the embedded-browser dialog when
        ''' absent (only if allowPrompt). Sessions are stored by the
        ''' Manager, encrypted at rest, and SHARED across plugins by
        ''' key — convention "{site}:{account}", e.g.
        ''' "myrealm:default". The Manager guarantees one in-flight
        ''' capture per key (concurrent callers share the result) and
        ''' blocks further prompts for a key after a cancelled or
        ''' failed capture until InvalidateWebSession is called or the
        ''' Manager restarts. Returns Nothing when no session is
        ''' available (absent + prompt not allowed/blocked/cancelled).
        ''' </summary>
        Function GetOrCaptureWebSessionAsync(sessionKey As String,
                                             startUrl As String,
                                             completionUrlPattern As String,
                                             cookieDomain As String,
                                             allowPrompt As Boolean) As Task(Of String)

        ''' <summary>
        ''' Remove the named shared session (e.g. on detecting expiry)
        ''' and clear its prompt-block, so the next
        ''' GetOrCaptureWebSessionAsync with allowPrompt can open the
        ''' login dialog again.
        ''' </summary>
        Sub InvalidateWebSession(sessionKey As String)

        ' --- web-capture: store-under-key + enumerate (Phase 7-7) ---
        ''' <summary>
        ''' Persist (encrypted) and cache cookieHeader under
        ''' sessionKey, stamped as captured by THIS plugin. The
        ''' capture→derive-key→store completion of
        ''' CaptureWebSessionAsync: the plugin captures a login, reads
        ''' the account identity to derive the key, then calls this to
        ''' save. Overwrites any existing session for the key and
        ''' clears its prompt-block. Gated by web-capture.
        ''' </summary>
        Sub StoreWebSession(sessionKey As String, cookieHeader As String)

        ''' <summary>
        ''' List THIS plugin's own stored web sessions (filtered by the
        ''' capturing plugin id) — key + timestamps only, never the
        ''' cookie header. Lets a plugin iterate its accounts for
        ''' discovery and failover. Gated by web-capture.
        ''' </summary>
        Function ListWebSessions() As IReadOnlyList(Of WebSessionSummary)

    End Interface

    ''' <summary>
    ''' OPT-IN side-interface (Phase 7-5b) for utility plugins that
    ''' use the shared web-session store: lets the Manager's Web
    ''' Sessions UI ask "does this session still work?" — a question
    ''' only the plugin can answer, since only it knows which URL
    ''' proves the session is alive and what a logged-out response
    ''' looks like. Implement it ALONGSIDE IUtilityPlugin; plugins
    ''' that don't implement it simply can't be validated from the
    ''' UI (everything else works unchanged — this interface is not
    ''' required).
    '''
    ''' THREADING: validation is invoked from the UI, OUTSIDE the
    ''' plugin's sequential event queue, so it can run concurrently
    ''' with HandleEventAsync. Implementations must be thread-safe
    ''' and side-effect-light (a single probe request; no state
    ''' mutation beyond what's already concurrency-safe).
    ''' </summary>
    Public Interface IWebSessionValidator

        ''' <summary>True when this plugin can validate the given
        ''' session key (typically a prefix check, e.g. keys starting
        ''' with "myrealm:").</summary>
        Function CanValidateWebSession(sessionKey As String) As Boolean

        ''' <summary>Probe the site with the supplied cookie header
        ''' and report whether the session still authenticates. Never
        ''' throws — wrap failures into a Failed result.</summary>
        Function ValidateWebSessionAsync(sessionKey As String,
                                         cookieHeader As String,
                                         context As IUtilityContext) As Task(Of WebSessionValidationResult)

    End Interface

    Public Enum WebSessionValidationState
        ''' <summary>The probe authenticated — session works.</summary>
        Valid
        ''' <summary>The site rejected the session (redirect to
        ''' sign-in or equivalent) — a fresh login is needed.</summary>
        Expired
        ''' <summary>The check itself couldn't run or couldn't reach a
        ''' verdict (network error, no probe endpoint known yet, …) —
        ''' says nothing about the session either way.</summary>
        Failed
    End Enum

    Public Class WebSessionValidationResult
        Public Property State As WebSessionValidationState
        ''' <summary>Human-readable detail for the UI (e.g.
        ''' "HTTP 200, Name input present").</summary>
        Public Property Detail As String
    End Class

    ''' <summary>
    ''' OPT-IN side-interface (Phase 7-6) for utility plugins that can
    ''' scrape importable records out of an authenticated web portal
    ''' — e.g. lo-myrealm enumerating the realms a myrealm session
    ''' can reach, with their customer/provider keys, for import into
    ''' a game plugin's shared-config (Shared Resources). Implement it
    ''' ALONGSIDE IUtilityPlugin; plugins that don't implement it
    ''' simply can't drive portal onboarding (everything else works
    ''' unchanged — not required).
    '''
    ''' Deliberately GAME-AGNOSTIC: a record names the target game
    ''' plugin + shared-config key and carries a flat field map keyed
    ''' by THAT game plugin's own shared-config field keys, so the
    ''' Manager writes records into 5h shared-config groups with no
    ''' game-specific knowledge. The plugin supplies the game-specific
    ''' mapping in its implementation.
    '''
    ''' THREADING: like IWebSessionValidator, discovery is driven from
    ''' the UI / onboarding flow, OUTSIDE the plugin's sequential
    ''' event queue. Implementations must be thread-safe and treat the
    ''' portal as READ-ONLY (GETs only — no writes back). Never throws
    ''' — return an empty list on any failure.
    ''' </summary>
    Public Interface IWebPortalDataProvider

        ''' <summary>True when this plugin can enumerate records for
        ''' the given session key (typically a prefix check, e.g. keys
        ''' starting with "myrealm:").</summary>
        Function CanProvideForSession(sessionKey As String) As Boolean

        ''' <summary>Sign in if needed (the plugin obtains its own
        ''' session via the shared store — allowPrompt opens the login
        ''' dialog when none is stored, which is onboarding's common
        ''' first-time case), then walk the authenticated portal and
        ''' return every importable record the session can reach.
        ''' Read-only (GETs only). Never throws — wrap failures into an
        ''' empty list (log via context).</summary>
        Function DiscoverRecordsAsync(sessionKey As String,
                                      allowPrompt As Boolean,
                                      context As IUtilityContext) As Task(Of IReadOnlyList(Of WebPortalImportRecord))

        ''' <summary>Force a FRESH interactive login (open the capture
        ''' dialog even when sessions already exist), derive this
        ''' account's key from the authenticated portal, store it via
        ''' context.StoreWebSession, and return the account label (the
        ''' key suffix) for the UI. Nothing on cancel/failure. Distinct
        ''' from DiscoverRecordsAsync's first-time capture, which reuses
        ''' any stored session — this always opens a new capture so the
        ''' operator can add ANOTHER account. Never throws. (Phase 7-7;
        ''' additive — lo-myrealm, the only implementer, ships with it,
        ''' so no ContractsVersion bump.)</summary>
        Function AddAccountAsync(context As IUtilityContext) As Task(Of String)

    End Interface

    ''' <summary>
    ''' One importable record scraped from a web portal, shaped so the
    ''' Manager can write it into a game plugin's shared-config group
    ''' (5h Shared Resources) with no game-specific knowledge.
    '''
    ''' Fields is keyed by the TARGET game plugin's own shared-config
    ''' field keys (from ISharedConfigProvider.GetSharedConfigSchema)
    ''' — e.g. Last Oasis: "CustomerKey", "ProviderKey", "RealmName".
    ''' The Manager dedups/upserts against existing groups by matching
    ''' the relevant field(s) and creates a new group only when none
    ''' matches.
    '''
    ''' One record == one shared-config group. lo-myrealm emits ONE
    ''' record per (customer key, provider key) pair, so a realm
    ''' hosted from several providers/locations becomes several groups
    ''' that share a RealmName but differ by ProviderKey — the design
    ''' that avoids a list-typed shared-config schema (Phase 7-6).
    ''' </summary>
    Public Class WebPortalImportRecord
        ''' <summary>Target game plugin id (e.g. "lastoasis").</summary>
        Public Property GameId As String
        ''' <summary>Target shared-config key (e.g. "realm").</summary>
        Public Property SharedConfigKey As String
        ''' <summary>Field values keyed by the target game plugin's
        ''' shared-config field keys. Sensitive values (keys) ride in
        ''' plaintext here; the Manager encrypts them at rest on write,
        ''' same as any shared-config field.</summary>
        Public Property Fields As New Dictionary(Of String, String)
        ''' <summary>The subset of Fields keys that constitute this
        ''' record's IDENTITY for dedup/upsert. The Manager matches a
        ''' candidate against existing shared-config groups on ALL of
        ''' these keys (plaintext, Ordinal); a full match means "same
        ''' entity, update it", no match means "new, create it".
        ''' lo-myrealm sets {"CustomerKey","ProviderKey"} so each
        ''' (customer, provider) pair is its own group. The Manager
        ''' stays generic — it never hard-codes a field name.</summary>
        Public Property MatchFieldKeys As New List(Of String)
        ''' <summary>The display name the Manager should give the
        ''' shared-config group (the plugin composes it because it
        ''' owns the field semantics). lo-myrealm uses "{RealmName}
        ''' ({UsedBy})" when the provider label adds information, else
        ''' the bare RealmName — keeping per-provider groups
        ''' distinguishable in pickers while History reads the
        ''' canonical RealmName field.</summary>
        Public Property SuggestedDisplayName As String
        ''' <summary>Optional human label distinguishing this record
        ''' from sibling records of the same realm — for lo-myrealm,
        ''' the provider key's "Used by" label (e.g. "selfhosted"). The
        ''' Manager uses it to compose a per-group display name.
        ''' Nothing when the portal gave no label.</summary>
        Public Property UsedBy As String
        ''' <summary>Trace/debug only — a portal-side identifier for
        ''' this record (lo-myrealm: the realm_id). Not written to the
        ''' shared-config group (the schema has no field for it);
        ''' surfaced in discovery logs to aid diagnosis.</summary>
        Public Property SourceRef As String
    End Class

    ''' <summary>
    ''' A Manager-side utility plugin. Implementations are discovered
    ''' by PluginRegistry alongside game plugins, hot-reload the same
    ''' way, and are hosted by UtilityPluginHost (lifecycle + queued,
    ''' isolated event dispatch).
    ''' </summary>
    Public Interface IUtilityPlugin

        ''' <summary>Stable identity; MUST match the manifest id.</summary>
        ReadOnly Property PluginId As String

        ReadOnly Property DisplayName As String

        ''' <summary>Event kinds this plugin wants. The host only
        ''' delivers these; an empty list means no event delivery
        ''' (a plugin can still act from InitializeAsync).</summary>
        ReadOnly Property SubscribedEvents As IReadOnlyList(Of UtilityEventKind)

        ''' <summary>Config fields rendered by the Manager's
        ''' Configure... dialog (SchemaFormBuilder). Return an empty
        ''' list for no configuration.</summary>
        Function GetConfigSchema() As List(Of ConfigFieldDescriptor)

        ''' <summary>Called once after load/reload, before any events.</summary>
        Function InitializeAsync(context As IUtilityContext) As Task

        ''' <summary>Called for each subscribed event. Exceptions are
        ''' caught, logged, and counted by the host; repeated failures
        ''' suspend delivery to this plugin until the next reload.</summary>
        Function HandleEventAsync(evt As UtilityEvent, context As IUtilityContext) As Task

        ''' <summary>Called before unload/reload. Keep it fast.</summary>
        Function ShutdownAsync() As Task

    End Interface

End Namespace
