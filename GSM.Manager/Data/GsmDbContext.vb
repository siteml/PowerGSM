Imports System
Imports System.Collections.Generic
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.EntityFrameworkCore.Design
Imports Microsoft.EntityFrameworkCore.Metadata.Builders

' ============================================================
'  GSM.Manager Data Layer
'
'  All entities, EF Core DbContext, fluent configurations,
'  and the design-time factory for EF migrations.
'
'  NOTE: Configuration class names use "EntityConfig" suffix
'  to avoid collisions with plugin DTO type names.
' ============================================================

Namespace GSM.Manager.Data

    ' ============================================================
    '  Entity classes
    ' ============================================================

    ''' <summary>
    ''' A managed machine running the GSM.Node service.
    ''' </summary>
    Public Class NodeEntity
        Public Property NodeId As String
        Public Property DisplayName As String
        Public Property HostAddress As String
        Public Property Port As Integer = 8765
        Public Property AuthToken As String

        ''' <summary>
        ''' Attach/detach toggle (True = attached, False =
        ''' detached). When detached, the manager skips the node
        ''' in background polling loops (status refresh, version
        ''' checks) and filters it out of new-installation
        ''' pickers, but preserves all configuration so the
        ''' operator can re-attach later without re-entering
        ''' anything. Existing log streams to a detached node
        ''' aren't actively cancelled — they continue until the
        ''' TCP connection drops or the operator closes the
        ''' viewer manually. Explicit operator actions (manual
        ''' Start/Stop/Restart from the InstancePanel, opening a
        ''' log viewer) are NOT gated by this flag; only
        ''' background polling is. Defaults to True so new nodes
        ''' are pollable on creation. Property name kept as
        ''' "IsEnabled" rather than "IsAttached" to avoid an EF
        ''' column-rename migration; the attach/detach vocabulary
        ''' lives in UI labels only.
        ''' </summary>
        Public Property IsEnabled As Boolean = True
        Public Property LastSeenUtc As DateTime
        Public Property OsDescription As String

        ''' <summary>
        ''' Node-wide ceiling on concurrent coordinated restarts
        ''' across all installations hosted on this node. Zero
        ''' means "no node-wide limit; installation-scoped limits
        ''' apply". Positive values cap total simultaneous
        ''' restarts (e.g. 1 forces absolute serialisation across
        ''' a shared box running LO + Factorio).
        ''' </summary>
        Public Property MaxConcurrentRestarts As Integer = 0

        ''' <summary>
        ''' Phase 5f-2 — last protocol version observed from this
        ''' node's /api/version response. Cached in the DB so the
        ''' Manager can render the protocol-compatibility indicator
        ''' on a node-detail panel without waiting for a fresh
        ''' round trip every time the panel opens.
        '''
        ''' Nullable: Nothing until the Manager has successfully
        ''' contacted the node since this column was added (or
        ''' since the node entity was created). Compared against
        ''' GSM.Node.Api.NodeApiContract.ProtocolVersion to decide
        ''' the indicator state. Same = silent, Manager newer =
        ''' yellow, Node newer = yellow, contact failure = red.
        '''
        ''' Refreshed on every successful /api/version call (cheap
        ''' — the endpoint is unauthenticated and returns a small
        ''' JSON body), so a node upgraded out from under the
        ''' Manager is detected at most one panel-open later.
        ''' </summary>
        Public Property LastSeenProtocolVersion As Integer?

        Public Overridable Property Installations As ICollection(Of InstallationEntity)
    End Class

    ''' <summary>
    ''' A set of game server files on a specific node.
    ''' One installation can serve multiple instances (e.g. Last Oasis).
    ''' </summary>
    Public Class InstallationEntity
        Public Property InstallationId As String
        Public Property GameId As String
        Public Property DisplayName As String
        Public Property NodeId As String
        Public Property InstallPath As String
        Public Property InstallMethod As String
        Public Property InstalledVersion As String
        Public Property SteamCredentialId As String

        ''' <summary>
        ''' Phase 5h — optional link to a SharedConfigGroupEntity
        ''' row, used by plugins that implement
        ''' ISharedConfigProvider to share config fields across
        ''' multiple installations (LO's Realm being the canonical
        ''' case). When set, the manager merges the group's
        ''' ConfigJson into InstanceConfig.CustomFields with lower
        ''' precedence than the installation's own ConfigJson, so
        ''' a per-installation override is still possible. Null
        ''' for installations of plugins that don't implement
        ''' ISharedConfigProvider, and for installations the user
        ''' has explicitly chosen to leave unlinked.
        ''' </summary>
        Public Property SharedConfigGroupId As String

        Public Property ConfigJson As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime

        ''' <summary>
        ''' Phase 5 — last value the VersionCheckService observed
        ''' from upstream. May differ from InstalledVersion (which
        ''' tracks what's actually installed) and is what triggers
        ''' version-mismatch rules. The poll service compares each
        ''' newly-fetched value against this; if changed, it both
        ''' updates this column and raises a mismatch event.
        '''
        ''' Null/empty until the first successful poll — a freshly
        ''' installed installation has InstalledVersion populated
        ''' but no LatestKnownVersion until VersionCheckService runs
        ''' for the first time.
        ''' </summary>
        Public Property LatestKnownVersion As String

        ''' <summary>
        ''' Phase 5 — timestamp of the last successful version check.
        ''' Used by the InstallationPanel for "Checked X minutes ago"
        ''' display, and by the polling service to throttle (skip
        ''' installations checked within the last poll interval to
        ''' tolerate rapid Manager restarts without re-polling
        ''' every installation immediately on each restart).
        '''
        ''' Nullable: Nothing until the first successful poll. Failed
        ''' polls do NOT update this — only successful ones — so an
        ''' installation that's been failing checks for a while shows
        ''' an obviously-stale timestamp in the UI.
        ''' </summary>
        Public Property LastVersionCheckUtc As DateTime?

        ''' <summary>
        ''' When true, the node runs every .exe under _CommonRedist
        ''' after a SteamCMD install completes (VC++, DirectX, etc.).
        ''' Off by default because most target machines already have
        ''' these installed, and without an elevated node service each
        ''' redistributable triggers a UAC prompt.
        ''' </summary>
        Public Property RunCommonRedist As Boolean

        ''' <summary>
        ''' Max instances from this installation that can be in
        ''' the "restarting" phase at once. Defaults to 1 so the
        ''' typical shared-install use case (LO's four realms
        ''' behind one install) gets safe sequential behaviour
        ''' automatically. A node-wide override on NodeEntity
        ''' takes precedence when set.
        ''' </summary>
        Public Property MaxConcurrentRestarts As Integer = 1

        Public Overridable Property Node As NodeEntity
        Public Overridable Property Instances As ICollection(Of InstanceEntity)
        Public Overridable Property SharedConfigGroup As SharedConfigGroupEntity
    End Class

    ''' <summary>
    ''' A running (or configured) game server instance.
    ''' Belongs to exactly one installation.
    ''' </summary>
    Public Class InstanceEntity
        Public Property InstanceId As String
        Public Property InstallationId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property ConfigJson As String
        Public Property ExeOverride As String
        Public Property AutoStart As Boolean = False
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime

        ''' <summary>
        ''' Position of this instance within its installation's
        ''' sibling list. Lower values come first. Used by the
        ''' Restart Schedule stagger feature to compute per-instance
        ''' offsets, and by the installation panel to display
        ''' instances in user-controlled order.
        '''
        ''' Default 0 is assigned to brand-new entities that haven't
        ''' been placed yet; the migration that introduced this
        ''' column backfilled existing rows with a stable ordering
        ''' based on CreatedUtc. New inserts should use
        ''' GsmDataExtensions.NextSortOrder for the target
        ''' installation to avoid collisions.
        ''' </summary>
        Public Property SortOrder As Integer = 0

        ' ---- Restart scheduling (hybrid quick-config) ----
        '
        ' These fields drive the "Restart Schedule" section on
        ' the EditInstance form. When RestartEnabled is true, a
        ' corresponding AutomationRule is materialised (created
        ' or updated) with RuleId == RestartRuleId. The rule is
        ' fully visible + editable in the Automation Rules
        ' window; edits that stay within what the simple UI can
        ' express round-trip, edits beyond that cause the simple
        ' UI to gray out and direct the user to the rule editor.

        ''' <summary>
        ''' Master on/off for scheduled restarts on this instance.
        ''' When toggled off, the generated rule is deleted
        ''' (not merely disabled) to keep the rules list clean.
        ''' </summary>
        Public Property RestartEnabled As Boolean = False

        ''' <summary>
        ''' Cron expression for when this instance wants to
        ''' restart. The coordinator serialises concurrent
        ''' firings via installation/node semaphores, so two
        ''' instances with identical crons won't both run at
        ''' once — they queue in acquisition order.
        ''' </summary>
        Public Property RestartCron As String

        ''' <summary>
        ''' FK to the auto-generated AutomationRule. Null when
        ''' RestartEnabled is false. When non-null, the rule
        ''' with this RuleId is the materialisation of the
        ''' quick-config fields above. Stored so the Manager
        ''' can round-trip edits without guessing which rule
        ''' belongs to which instance.
        ''' </summary>
        Public Property RestartRuleId As String

        ''' <summary>
        ''' User-defined logical grouping label. Lets users tag
        ''' instances as part of a "realm", "cluster", "production
        ''' tier", etc. without the engine knowing what the tag
        ''' means. Used by RuleScope.InstanceSet to resolve
        ''' "all instances in this set" — sets can span
        ''' installations and nodes.
        '''
        ''' Game-agnostic and entirely user-driven: no plugin
        ''' opt-in needed, no schema for what tags exist. Auto-
        ''' complete in EditInstanceForm offers existing distinct
        ''' values from the DB so users can stay consistent.
        ''' Comparison is case-sensitive at query time.
        ''' </summary>
        Public Property InstanceSetTag As String

        Public Overridable Property Installation As InstallationEntity
    End Class

    ''' <summary>
    ''' A persisted automation rule.
    ''' Trigger, conditions, and action are stored as JSON.
    '''
    ''' GameFilter is a top-level filter (not embedded in the
    ''' rule's JSON) so the engine can index/query on it later
    ''' without parsing every rule. Applies to multi-instance
    ''' scopes; ignored for Instance scope.
    ''' </summary>
    Public Class AutomationRuleEntity
        Public Property RuleId As String
        Public Property RuleName As String
        Public Property IsEnabled As Boolean = True
        Public Property ScopeKind As String
        Public Property TargetId As String
        Public Property GameFilter As String
        Public Property TriggerJson As String
        Public Property ConditionsJson As String
        Public Property ActionJson As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime

        ''' <summary>
        ''' Display position in the Automation Rules window's list.
        ''' Lower values come first. Like InstanceEntity.SortOrder,
        ''' default 0 is what brand-new rows get; the migration that
        ''' introduced this column should backfill existing rows with
        ''' a stable ordering based on CreatedUtc. New inserts should
        ''' use GsmDataExtensions.NextRuleSortOrder to land at the end
        ''' of the existing list.
        '''
        ''' Has no effect on rule firing semantics — it's purely a
        ''' display preference. Two rules whose triggers fire at the
        ''' same instant still queue based on the engine's internal
        ''' ordering (cron tick order, condition-result order, etc.),
        ''' not on this column.
        ''' </summary>
        Public Property SortOrder As Integer = 0
    End Class

    ''' <summary>
    ''' Steam credentials encrypted with DPAPI.
    ''' Password is stored as a byte array — only decryptable
    ''' on the same Windows account that encrypted it.
    ''' </summary>
    Public Class SteamCredentialEntity
        Public Property CredentialId As String
        Public Property DisplayName As String
        Public Property Username As String
        Public Property EncryptedPassword As Byte()
        Public Property IsAnonymous As Boolean
    End Class

    ''' <summary>
    ''' Notification plugin configuration (Discord bot tokens, webhook URLs, etc).
    ''' </summary>
    Public Class NotificationPluginEntity
        Public Property PluginId As String
        Public Property DisplayName As String
        Public Property IsEnabled As Boolean = True
        Public Property ConfigJson As String
    End Class

    ''' <summary>
    ''' Notification subscription — which events route to which plugin.
    ''' </summary>
    Public Class NotificationSubscriptionEntity
        Public Property SubscriptionId As String
        Public Property PluginId As String
        Public Property EventName As String
        Public Property ScopeKind As String
        Public Property TargetId As String
        Public Property RoutingHintsJson As String
        Public Property IsEnabled As Boolean = True
    End Class

    ''' <summary>
    ''' Execution history for automation rules.
    ''' </summary>
    Public Class RuleExecutionEntity
        Public Property ExecutionId As String
        Public Property RuleId As String
        Public Property StartedAtUtc As DateTime
        Public Property CompletedAtUtc As DateTime?
        Public Property TriggerReason As String
        Public Property ConditionResultsJson As String
        Public Property ActionResultJson As String
        Public Property WasSkipped As Boolean
        Public Property SkipReason As String
    End Class

    ''' <summary>
    ''' Phase 5l-3 — one row per self-update apply attempt. Powers
    ''' Help → Update history. Written by UpdateOrchestrator: a
    ''' Success row on the post-update startup, a Failed row when a
    ''' prior apply left an apply-error.log behind.
    ''' </summary>
    Public Class UpdateHistoryEntity
        Public Property HistoryId As String
        Public Property AppliedAtUtc As DateTime
        Public Property FromVersion As String
        Public Property ToVersion As String
        ''' <summary>"Success" or "Failed".</summary>
        Public Property Outcome As String
        ''' <summary>Optional detail — error text for failures.</summary>
        Public Property Detail As String
    End Class

    ''' <summary>
    ''' Phase 6-2 — a GitHub location the Manager can browse for
    ''' plugins (.vb files whose inline &lt;plugin&gt; manifests are
    ''' parsed into a catalog). The official source (siteml/PowerGSM
    ''' at GSM.PluginsSource/) is seeded on first run and cannot be
    ''' deleted, only disabled.
    ''' </summary>
    Public Class PluginSourceEntity
        Public Property SourceId As String
        Public Property DisplayName As String
        ''' <summary>GitHub owner (user or org), e.g. "siteml".</summary>
        Public Property Owner As String
        Public Property Repo As String
        ''' <summary>Folder within the repo holding plugin .vb files
        ''' (empty = repo root).</summary>
        Public Property RepoPath As String
        ''' <summary>Branch to read from, e.g. "master".</summary>
        Public Property Branch As String
        ''' <summary>True only for the seeded official source — its
        ''' plugins may use bare ids; it can't be deleted.</summary>
        Public Property IsOfficial As Boolean
        Public Property IsEnabled As Boolean
        Public Property LastFetchedUtc As DateTime?
    End Class

    ''' <summary>
    ''' A shared web session held by the Manager on behalf of utility
    ''' plugins (Phase 7-5). Keyed by a plugin-chosen convention
    ''' "{site}:{account}" (e.g. "myrealm:default"); the cookie
    ''' header is DPAPI-encrypted at rest (CurrentUser scope, same as
    ''' Steam credentials).
    ''' </summary>
    Public Class WebSessionEntity
        Public Property SessionKey As String
        Public Property EncryptedCookieHeader As Byte()
        Public Property CapturedAtUtc As DateTime
        ''' <summary>Plugin id that performed the capture — audit
        ''' only; any web-capture plugin may read the session.</summary>
        Public Property CapturedByPluginId As String
        Public Property LastUsedUtc As DateTime?
    End Class

    ''' <summary>
    ''' A named set of fields that notifications are ALLOWED to expose.
    ''' Destinations reference a profile to decide how much detail their
    ''' messages contain — e.g. a Public profile strips IPs, paths, keys;
    ''' an Admin profile shows everything. Seeded on first run with
    ''' Public / Admin defaults.
    ''' </summary>
    Public Class VisibilityProfileEntity
        Public Property ProfileId As String
        Public Property DisplayName As String
        ''' <summary>JSON array of field names from NotificationField enum.</summary>
        Public Property AllowedFieldsJson As String
        Public Property IsBuiltIn As Boolean
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>
    ''' A single notification destination. Currently supports Discord
    ''' webhooks; future transports (Slack, Telegram, email) will add
    ''' more TransportKind values and parse payloads accordingly.
    ''' </summary>
    Public Class NotificationDestinationEntity
        Public Property DestinationId As String
        Public Property DisplayName As String
        Public Property Enabled As Boolean

        ''' <summary>e.g. "DiscordWebhook" — selects the transport impl.</summary>
        Public Property TransportKind As String

        ''' <summary>Transport-specific config as JSON (webhook URL etc).</summary>
        Public Property TransportConfigJson As String

        ''' <summary>JSON array of NotificationEventType values to send.</summary>
        Public Property EnabledEventTypesJson As String

        ''' <summary>
        ''' JSON array of installation IDs this destination cares about.
        ''' Empty/null = all installations. Destinations filter at both
        ''' installation and instance level — the two filters AND.
        ''' </summary>
        Public Property InstallationFilterJson As String

        ''' <summary>
        ''' JSON array of instance IDs this destination cares about.
        ''' Empty/null = all instances. Applied on top of the installation
        ''' filter — an event must pass both to be sent.
        ''' </summary>
        Public Property InstanceFilterJson As String

        ''' <summary>
        ''' Phase 5n — JSON array of node IDs this destination cares
        ''' about. Part of the union-of-includes scope model: an event
        ''' is in scope if it matches ANY populated scope dimension
        ''' (node / installation / instance / set); all four empty =
        ''' all instances. Honoured at send time from Phase 5n-2 on.
        ''' </summary>
        Public Property NodeFilterJson As String

        ''' <summary>
        ''' Phase 5n — JSON array of InstanceSetTag values this
        ''' destination cares about. Case-sensitive match (parity with
        ''' RuleScope.InstanceSet). Part of the union-of-includes scope
        ''' model (see NodeFilterJson).
        ''' </summary>
        Public Property InstanceSetFilterJson As String

        Public Property VisibilityProfileId As String

        ''' <summary>
        ''' Optional per-event-type template overrides as JSON:
        ''' { "InstanceStarted": "🟢 {InstanceName} is up on {NodeName}", ... }
        ''' Keys not present fall back to built-in defaults.
        ''' </summary>
        Public Property TemplateOverridesJson As String

        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ' ============================================================
    '  Discord bot integration — Phase 5d
    '
    '  Two tables back the bot plugin: a single-row config holding
    '  the bot's encrypted token (DPAPI, same shape as Steam
    '  credentials) and a panels table where each row describes
    '  one persistent control-panel message in a Discord channel.
    '
    '  The bot identity is intentionally global to one PowerGSM
    '  installation — a single Discord application/token reused
    '  across every guild the operator invites the bot into.
    '  Per-guild settings (channels, panels, role mappings, etc.)
    '  travel on the panel rows themselves; their GuildId column
    '  is the discriminator. This matches how Discord bot
    '  applications actually work (one token, many guilds) and
    '  avoids forcing the operator to register a separate bot per
    '  guild.
    '
    '  ConfigId is fixed at "default" for v1. Keeping it a column
    '  rather than hard-coding a key means a future "multiple bot
    '  identities" feature (one token per environment, say) can
    '  add rows without a schema change.
    ' ============================================================

    ''' <summary>
    ''' Discord bot configuration — single-row per identity.
    ''' Holds the encrypted bot token used to log in to Discord
    ''' and the enabled flag controlling whether the bot connects
    ''' on Manager startup.
    ''' </summary>
    Public Class DiscordBotConfigEntity
        ''' <summary>
        ''' Stable identifier for this bot identity. v1 uses the
        ''' literal "default" — there's exactly one row. The
        ''' column lets us add additional identities later
        ''' without a schema change.
        ''' </summary>
        Public Property ConfigId As String

        ''' <summary>
        ''' Friendly name shown in the configuration UI ("PowerGSM
        ''' Bot", etc.). Doesn't need to match the bot's Discord
        ''' username — purely cosmetic for the operator.
        ''' </summary>
        Public Property DisplayName As String

        ''' <summary>
        ''' DPAPI-encrypted bot token. Encrypted via
        ''' CredentialService.ProtectString; only decryptable on
        ''' the same Windows account that wrote it. Empty (zero-
        ''' length array) when no token is yet configured — the
        ''' bot won't attempt to connect in that state.
        ''' </summary>
        Public Property EncryptedToken As Byte()

        ''' <summary>
        ''' Master on/off. When False, the bot won't connect even
        ''' if a token is stored — useful for temporarily silencing
        ''' the bot without losing its token. When True with no
        ''' token, the plugin logs a warning at startup and stays
        ''' disconnected.
        ''' </summary>
        Public Property Enabled As Boolean = True

        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>
    ''' One persistent control panel — a rich-embed message the
    ''' bot maintains in a Discord channel showing instance state
    ''' and offering a Manage button (the button stub ships in
    ''' 5d-1; the management ephemeral flow ships in 5d-2).
    '''
    ''' MessageId is null until the bot has successfully posted
    ''' the panel for the first time. On subsequent Manager
    ''' restarts the bot edits the existing message in place —
    ''' preserving message permalinks — rather than re-posting.
    ''' If the message has been manually deleted from Discord
    ''' (channel purge, operator removed it, etc.), the bot
    ''' detects the 404 on edit and re-posts, refreshing
    ''' MessageId here.
    '''
    ''' ScopeKind values (string for forward compat — not bound
    ''' to RuleScope on purpose; rule scopes are evaluation
    ''' targets, panel scopes are display filters):
    '''   "AllInstances"  → ScopeTargetId is ignored
    '''   "Game"          → ScopeTargetId is a GameId
    '''   "Installation"  → ScopeTargetId is an InstallationId
    '''   "InstanceSet"   → ScopeTargetId is an InstanceSetTag value
    ''' </summary>
    Public Class DiscordPanelEntity
        Public Property PanelId As String
        Public Property GuildId As String
        Public Property ChannelId As String

        ''' <summary>
        ''' Message ID populated after the first successful post.
        ''' Nullable so a freshly-saved panel reads as "needs
        ''' posting" until the bot's next refresh cycle.
        ''' </summary>
        Public Property MessageId As String

        Public Property DisplayName As String

        ''' <summary>
        ''' Panel kind discriminator (Phase 5k). "InstanceManager"
        ''' (default) renders the instance status / Manage panel;
        ''' "PlayerList" renders the online-players roster panel.
        ''' Short string for the same reason as ScopeKind / GroupingKind
        ''' (no EF int-enum coupling). Defaults to "InstanceManager" so
        ''' every pre-5k row reads as the original panel kind.
        ''' </summary>
        Public Property PanelKind As String = "InstanceManager"

        Public Property ScopeKind As String
        Public Property ScopeTargetId As String

        ''' <summary>
        ''' Per-panel polling interval (seconds) for time-relative
        ''' fields like player count and "next restart" countdowns
        ''' — things that drift without firing NotificationEmitter
        ''' events. Default 60 keeps Discord rate limits comfortable
        ''' across multiple panels while still feeling alive. Event-
        ''' driven refreshes (instance start/stop/crash) trigger
        ''' independently and are coalesced to at most one edit per
        ''' panel per 5s by the plugin runtime.
        ''' </summary>
        Public Property RefreshIntervalSeconds As Integer = 60

        ''' <summary>
        ''' Per-row layout as a JSON-serialised list of element
        ''' descriptors (Phase 5d-5 item 3). NULL means "use the
        ''' hardcoded default layout" — byte-identical to the v1
        ''' rendering, so existing rows that predate this column
        ''' read correctly without backfill. Shape:
        '''   { "elements": [ { "type": "StateEmoji" }, ... ] }
        ''' Element classes are defined alongside the renderer in
        ''' DiscordBotPlugin.vb; serialisation goes through
        ''' PanelLayoutSerializer (a polymorphic JSON layer over
        ''' the otherwise-flat element classes).
        ''' </summary>
        Public Property LayoutJson As String

        ''' <summary>
        ''' Whole-panel grouping discriminator (Phase 5d-5 item 3).
        ''' Stored as a short string for the same reason as
        ''' ScopeKind: avoids EF int-enum coupling and keeps
        ''' migrations straightforward when new kinds are added.
        ''' Values: "None", "ByNode", "ByGame", "ByNodeThenGame".
        ''' Defaults to "None" so existing rows read as flat.
        ''' Independent of LayoutJson: a flat panel can have a
        ''' custom row layout, and a default-layout panel can be
        ''' grouped — the two decisions are orthogonal.
        ''' </summary>
        Public Property GroupingKind As String = "None"

        ''' <summary>
        ''' Player-list panels (PanelKind = "PlayerList") hide instance
        ''' groups with nobody online by default, so empty servers don't
        ''' waste vertical space. Set True to show every in-scope
        ''' instance regardless of occupancy. Ignored by InstanceManager
        ''' panels. Defaults False (hide empties).
        ''' </summary>
        Public Property ShowEmptyGroups As Boolean = False

        ''' <summary>
        ''' Player-list panels: when True, each player row also shows
        ''' the player's join time as a relative Discord timestamp
        ''' ("joined <t:…:R>"), sourced from PlayerSession.JoinedUtc.
        ''' Ignored by InstanceManager panels. Defaults False.
        ''' </summary>
        Public Property ShowJoinTime As Boolean = False

        ''' <summary>
        ''' Player-list panels: when True, the total online count is
        ''' appended to the panel title ("Players online (12)") on top
        ''' of the footer total. Ignored by InstanceManager panels.
        ''' Defaults False.
        ''' </summary>
        Public Property ShowTotalInTitle As Boolean = False

        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>
    ''' Per-guild role-to-permission mapping (Phase 5d-3). Drives
    ''' the bot's command-permission resolution: when a user
    ''' clicks an action button on a panel, the bot walks the
    ''' user's role list, intersects with this table for the
    ''' originating guild, and returns the highest permission tier
    ''' found. Roles not in this table contribute nothing — the
    ''' Everyone tier is the implicit default for unmapped roles,
    ''' so this table only stores elevations.
    '''
    ''' Replaces the hardcoded "PowerGSM Operator" role name from
    ''' 5d-2's TestOperatorRoleName Const, which couldn't express
    ''' multi-tier permissions or differ between guilds. Multi-
    ''' guild operators (one bot, several Discord servers) can
    ''' now grant elevations on a per-guild basis without their
    ''' role names colliding.
    '''
    ''' Composite PK on (GuildId, PanelId, RoleId): at most one
    ''' mapping row per role per (guild, panel). Permission is
    ''' stored as the integer value of GSM.Notification.CommandPermission
    ''' so the natural ordering (Everyone=0, ServerOperator=1,
    ''' Administrator=2) is usable directly for the "highest tier
    ''' found" lookup without enum-name parsing per interaction.
    ''' RoleName is a display snapshot — used by the configuration
    ''' UI to render role names without a fresh Discord query;
    ''' the actual permission match uses RoleId only, since role
    ''' names can be changed in Discord without our knowledge.
    '''
    ''' PanelId scope discriminator (Phase 5d-5 item 4):
    '''   • Empty string "" = guild-default mapping (the v1
    '''     behaviour; applies to every panel in the guild that
    '''     doesn't override).
    '''   • Non-empty = panel-scoped override (matches DiscordPanelEntity.PanelId).
    ''' Empty-string sentinel rather than NULL because SQLite's
    ''' composite PK semantics treat NULL ≠ NULL, which would let
    ''' multiple guild-default rows for the same role coexist —
    ''' breaking the "at most one mapping per role per scope"
    ''' invariant. Sentinel keeps SQLite enforcing uniqueness
    ''' correctly at the cost of one If(value, "") in the
    ''' resolver and load paths.
    ''' </summary>
    Public Class DiscordRoleMappingEntity
        Public Property GuildId As String

        ''' <summary>
        ''' Empty string for the guild-default mapping; a panel ID
        ''' for a panel-scoped override. See the class summary for
        ''' the empty-string-sentinel rationale.
        ''' </summary>
        Public Property PanelId As String = ""

        Public Property RoleId As String
        Public Property RoleName As String

        ''' <summary>
        ''' CommandPermission as an Integer. 1 = ServerOperator,
        ''' 2 = Administrator. Everyone (0) is never stored — it's
        ''' the implicit default for unmapped roles, and the UI
        ''' filters it out of the dropdown for the same reason.
        ''' Stored as Integer rather than the enum's string name
        ''' so the natural ordering survives without parsing.
        ''' </summary>
        Public Property Permission As Integer

        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ' ============================================================
    '  Session history — Round B of cross-instance entity tracking
    '
    '  Three tables work together to answer questions like
    '  "what did Alice type last Tuesday while she was playing
    '  on tile T3" even if that tile has since migrated between
    '  instances or to a different node.
    '
    '  The primary key for cross-instance correlation is
    '  SessionIdentity, produced by the game plugin's log parser.
    '  For games with no migration concept, session identity
    '  collapses to "{gameId}:{instanceId}" and everything still
    '  works — there's just one session per instance and no
    '  transitions to record.
    ' ============================================================

    ''' <summary>
    ''' A single chat message captured from a game server. Keyed
    ''' by SessionIdentity + TimestampUtc so queries like
    ''' "all chat on this tile ever" and "all chat today" are
    ''' both cheap. Retention is time-based (configurable via
    ''' AppSetting "ChatRetentionDays") so the table doesn't
    ''' grow without bound.
    ''' </summary>
    Public Class ChatMessageEntity
        Public Property MessageId As String
        Public Property SessionIdentity As String
        ''' <summary>Node that captured this message. Useful for
        ''' filtering "all chat across any tile on node X".</summary>
        Public Property NodeId As String
        ''' <summary>Instance that hosted the session when this
        ''' message was captured. May differ across messages of
        ''' the same SessionIdentity if the tile migrated.</summary>
        Public Property InstanceId As String
        Public Property TimestampUtc As DateTime

        ''' <summary>
        ''' In-game character name as it appeared in the chat line
        ''' — the same string that shows in-game. Stored as-text
        ''' per message so a historical row retains the name the
        ''' player was using when they spoke, even if they later
        ''' renamed their character on myrealm. Always populated.
        ''' </summary>
        Public Property DisplayName As String

        ''' <summary>
        ''' Resolved Steam/Xbox/etc. ID for the speaker at the
        ''' moment the chat line was parsed. Nothing for messages
        ''' whose speaker hadn't been identity-resolved yet (chat
        ''' arriving in the small window between a player's join
        ''' and their first Persisting autosave tick) and Nothing
        ''' for historical rows that pre-date Phase 5g-1 (the
        ''' column was added by migration with no backfill).
        ''' </summary>
        Public Property PlatformUserId As String

        ''' <summary>
        ''' Resolved CharacterId for the speaker, same provenance
        ''' caveats as PlatformUserId. Stable across renames — the
        ''' canonical durable identity for cross-rename history
        ''' queries ("every chat line from this character, no
        ''' matter what name they were going by at the time").
        ''' </summary>
        Public Property CharacterId As String

        Public Property Text As String
    End Class

    ''' <summary>
    ''' Aggregated per-player activity on a session. UPSERTed
    ''' on every join/leave observation: first join creates a
    ''' row, subsequent observations update LastSeenUtc. This is
    ''' the "who played on this tile ever" index and answers the
    ''' "last seen" query in O(1) lookup. Never pruned by
    ''' retention — the user wants this to survive until a realm
    ''' is actually nuked (realm_id changes → new SessionIdentity
    ''' → new row set).
    ''' </summary>
    Public Class PlayerSessionEntity
        Public Property PlayerSessionId As String
        Public Property SessionIdentity As String
        Public Property PlayerName As String
        Public Property FirstSeenUtc As DateTime
        Public Property LastSeenUtc As DateTime
        ''' <summary>Most recently observed instance that hosted
        ''' this player's session. For forensics / "who was
        ''' hosting when Alice logged off".</summary>
        Public Property LastHostInstanceId As String
    End Class

    ''' <summary>
    ''' Audit trail of which instance hosted which session-identity
    ''' and when. One row per (SessionIdentity, InstanceId) hosting
    ''' window. HostedUntilUtc is Nothing while the instance is
    ''' actively hosting; the row is closed out when the parser
    ''' observes a TileUnloaded event or the SessionIdentity
    ''' changes.
    '''
    ''' Not strictly required for the user-facing features, but
    ''' makes forensic questions answerable without trawling logs:
    ''' "which instance was hosting tile T at 03:17 last Tuesday".
    ''' </summary>
    Public Class SessionHostEntity
        Public Property HostId As String
        Public Property SessionIdentity As String
        Public Property InstanceId As String
        Public Property HostedFromUtc As DateTime
        ''' <summary>Nothing means still hosting.</summary>
        Public Property HostedUntilUtc As DateTime?
        ''' <summary>
        ''' Human-readable tile/session name at the time this row
        ''' was opened. Tile names can change across game updates
        ''' for the same underlying tile_id, so each hosting window
        ''' stamps the name as-seen and UI queries use the most
        ''' recent row's name when resolving SessionIdentity to a
        ''' display label. Nothing when the plugin doesn't supply
        ''' one (e.g. games without migration semantics).
        ''' </summary>
        Public Property TileName As String
    End Class

    ''' <summary>
    ''' Append-only log of every individual player join and leave
    ''' observation. Added in Round D1 to support the History
    ''' window's timeline view and snapshot mode ("who was online
    ''' at this moment") — both need the individual transitions,
    ''' not the aggregated first-seen/last-seen in PlayerSessions.
    '''
    ''' Written alongside PlayerSessions on every observation:
    ''' PlayerSessions = per-player summary, PlayerActivity =
    ''' full event stream.
    '''
    ''' Retention: NEVER pruned by time. This is identity-scoped
    ''' data (keyed by SessionIdentity) — the same class as
    ''' PlayerSessions and SessionHosts. Pruning by time would
    ''' break "last seen" lookups months or years after a player
    ''' was on, which defeats the reason the table exists. Rows
    ''' naturally become orphans when a realm's identity changes
    ''' (realm nuked → new realm_id → new SessionIdentity); that's
    ''' the only cleanup mechanism, and it's implicit.
    ''' </summary>
    Public Class PlayerActivityEntity
        Public Property ActivityId As String
        Public Property SessionIdentity As String
        Public Property NodeId As String
        Public Property InstanceId As String
        Public Property TimestampUtc As DateTime

        ''' <summary>
        ''' Raw name string as the parser saw it on the log line
        ''' that produced this event. On Last Oasis this is the
        ''' platform persona (Steam handle / Xbox gamertag from the
        ''' login URL) — distinct from the in-game character name.
        ''' On Factorio it's the character name (game has no
        ''' separate platform layer). Always populated; the durable
        ''' fallback the renderer falls back to when the resolved
        ''' identity columns below are NULL.
        ''' </summary>
        Public Property PlayerName As String

        ''' <summary>"join" or "leave" — stored as lowercase string
        ''' for future extensibility (kick, ban, etc.). Kept short
        ''' so index covering is cheap.</summary>
        Public Property EventKind As String

        ''' <summary>
        ''' Phase 5g-2: snapshot of the speaker's CharacterId at
        ''' the moment the event row was written, captured via a
        ''' wire call to the Node's /players endpoint. Stable
        ''' across in-game character name changes; the durable
        ''' join key for cross-rename history queries ("every
        ''' join/leave ever by this character, no matter what
        ''' name they were going by then"). NULL when the Node's
        ''' /players lookup missed or hadn't resolved CharacterId
        ''' yet (common on PlayerLeave events — the Node removes
        ''' the session from its in-memory dict on the same log
        ''' line the Manager is processing) and NULL for rows
        ''' that pre-date the migration that introduced this
        ''' column.
        ''' </summary>
        Public Property CharacterId As String

        ''' <summary>
        ''' Phase 5g-2: snapshot of the speaker's PlatformUserId
        ''' (Steam ID / Xbox LIVE ID / etc.) at write time. Same
        ''' provenance and NULL semantics as CharacterId — both
        ''' resolved together from the same /players row in
        ''' InstanceManager.PersistPlayerObservation.
        ''' </summary>
        Public Property PlatformUserId As String

        ''' <summary>
        ''' Phase 5g-2: snapshot of the speaker's in-game
        ''' DisplayName at write time. Stored alongside PlayerName
        ''' so History rendering doesn't need a live join against
        ''' a Manager-side mirror of the Node's players table —
        ''' the renderer just coalesces DisplayName ?? PlayerName
        ''' via IdentityFormatter, falling back to the raw
        ''' PlayerName for rows where DisplayName never resolved
        ''' (short LO sessions that ended before the autosave tick
        ''' bridged platform persona to display name) and for
        ''' rows that pre-date the migration.
        '''
        ''' Snapshot semantics on purpose: a character's chosen
        ''' name is generally stable over its lifetime, so the
        ''' snapshot is correct for nearly every player. The rare
        ''' myrealm-admin-rename case leaves old rows showing the
        ''' old name, which is arguably more honest about what
        ''' the player was actually called at the time.
        ''' </summary>
        Public Property DisplayName As String
    End Class

    ''' <summary>
    ''' Generic key-value store for manager-level preferences.
    ''' Introduced in Round B for ChatRetentionDays; reusable for
    ''' future global settings without schema churn. Not intended
    ''' for per-entity configuration — use ConfigJson fields on
    ''' the relevant entity for those.
    ''' </summary>
    Public Class AppSettingEntity
        Public Property SettingKey As String
        Public Property Value As String
    End Class

    ''' <summary>
    ''' Phase 5h — a shared-config group declared by a plugin
    ''' that implements ISharedConfigProvider. One row holds the
    ''' fields multiple installations of the same plugin share
    ''' (e.g. Last Oasis Realm: CustomerKey, ProviderKey,
    ''' RealmName). Installations point at the group via
    ''' Installation.SharedConfigGroupId; the manager merges the
    ''' group's ConfigJson into InstanceConfig.CustomFields with
    ''' lower precedence than the installation's own ConfigJson
    ''' at start-instance time, so plugins read merged values
    ''' transparently regardless of which layer supplied each.
    ''' </summary>
    Public Class SharedConfigGroupEntity
        ''' <summary>Manager-generated GUID. Primary key.</summary>
        Public Property GroupId As String

        ''' <summary>Matches Installation.GameId. Filters
        ''' which groups belong to which plugin so the
        ''' management UI shows only the relevant ones per
        ''' plugin context.</summary>
        Public Property PluginId As String

        ''' <summary>Matches the plugin's
        ''' ISharedConfigProvider.SharedConfigKey, e.g. "realm".
        ''' Persisted (rather than derived at runtime from the
        ''' plugin) so a database loaded without the owning
        ''' plugin still surfaces meaningful information about
        ''' what kind of group each row represents.</summary>
        Public Property GroupType As String

        ''' <summary>User-facing name shown in pickers and
        ''' management UI (e.g. "Site's World"). Required; the
        ''' UI uses this to refer to the group everywhere except
        ''' the internal GroupId.</summary>
        Public Property DisplayName As String

        ''' <summary>Field values for this group, serialised as
        ''' a JSON dictionary keyed by the plugin's
        ''' GetSharedConfigSchema field keys. Fields declared
        ''' IsSensitive in the schema are encrypted with a
        ''' sentinel-prefixed base64 wrapper (see
        ''' SharedConfigService for the encode/decode pair);
        ''' non-sensitive fields are stored in plaintext for
        ''' direct readability when dumping the database.</summary>
        Public Property ConfigJson As String

        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime

        ''' <summary>Reverse navigation — installations linked
        ''' to this group. Populated by EF Core via the FK
        ''' relationship configured on InstallationEntityConfig.
        ''' Typical use case: "show me all the installations on
        ''' this realm."</summary>
        Public Overridable Property Installations As ICollection(Of InstallationEntity)
    End Class

    ' ============================================================
    '  DbContext
    ' ============================================================

    Public Class GsmDbContext
        Inherits DbContext

        Public Sub New(options As DbContextOptions(Of GsmDbContext))
            MyBase.New(options)
        End Sub

        Public Property Nodes As DbSet(Of NodeEntity)
        Public Property Installations As DbSet(Of InstallationEntity)
        Public Property Instances As DbSet(Of InstanceEntity)
        Public Property AutomationRules As DbSet(Of AutomationRuleEntity)
        Public Property SteamCredentials As DbSet(Of SteamCredentialEntity)
        Public Property NotificationPlugins As DbSet(Of NotificationPluginEntity)
        Public Property NotificationSubscriptions As DbSet(Of NotificationSubscriptionEntity)
        Public Property RuleExecutions As DbSet(Of RuleExecutionEntity)
        Public Property VisibilityProfiles As DbSet(Of VisibilityProfileEntity)
        Public Property NotificationDestinations As DbSet(Of NotificationDestinationEntity)
        Public Property DiscordBotConfigs As DbSet(Of DiscordBotConfigEntity)
        Public Property DiscordPanels As DbSet(Of DiscordPanelEntity)
        Public Property DiscordRoleMappings As DbSet(Of DiscordRoleMappingEntity)
        Public Property ChatMessages As DbSet(Of ChatMessageEntity)
        Public Property PlayerSessions As DbSet(Of PlayerSessionEntity)
        Public Property SessionHosts As DbSet(Of SessionHostEntity)
        Public Property PlayerActivity As DbSet(Of PlayerActivityEntity)
        Public Property AppSettings As DbSet(Of AppSettingEntity)
        Public Property SharedConfigGroups As DbSet(Of SharedConfigGroupEntity)
        Public Property UpdateHistory As DbSet(Of UpdateHistoryEntity)
        Public Property PluginSources As DbSet(Of PluginSourceEntity)
        Public Property WebSessions As DbSet(Of WebSessionEntity)

        Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder)
            modelBuilder.ApplyConfiguration(New NodeEntityConfig())
            modelBuilder.ApplyConfiguration(New InstallationEntityConfig())
            modelBuilder.ApplyConfiguration(New InstanceEntityConfig())
            modelBuilder.ApplyConfiguration(New AutomationRuleEntityConfig())
            modelBuilder.ApplyConfiguration(New SteamCredentialEntityConfig())
            modelBuilder.ApplyConfiguration(New NotificationPluginEntityConfig())
            modelBuilder.ApplyConfiguration(New NotificationSubscriptionEntityConfig())
            modelBuilder.ApplyConfiguration(New RuleExecutionEntityConfig())
            modelBuilder.ApplyConfiguration(New VisibilityProfileEntityConfig())
            modelBuilder.ApplyConfiguration(New NotificationDestinationEntityConfig())
            modelBuilder.ApplyConfiguration(New DiscordBotConfigEntityConfig())
            modelBuilder.ApplyConfiguration(New DiscordPanelEntityConfig())
            modelBuilder.ApplyConfiguration(New DiscordRoleMappingEntityConfig())
            modelBuilder.ApplyConfiguration(New ChatMessageEntityConfig())
            modelBuilder.ApplyConfiguration(New PlayerSessionEntityConfig())
            modelBuilder.ApplyConfiguration(New SessionHostEntityConfig())
            modelBuilder.ApplyConfiguration(New PlayerActivityEntityConfig())
            modelBuilder.ApplyConfiguration(New AppSettingEntityConfig())
            modelBuilder.ApplyConfiguration(New SharedConfigGroupEntityConfig())
            modelBuilder.ApplyConfiguration(New UpdateHistoryEntityConfig())
            modelBuilder.ApplyConfiguration(New PluginSourceEntityConfig())
            modelBuilder.ApplyConfiguration(New WebSessionEntityConfig())
        End Sub

    End Class

    ' ============================================================
    '  Fluent configurations (EntityConfig suffix avoids collisions)
    ' ============================================================

    Public Class NodeEntityConfig
        Implements IEntityTypeConfiguration(Of NodeEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NodeEntity)) Implements IEntityTypeConfiguration(Of NodeEntity).Configure
            builder.HasKey(Function(n) n.NodeId)
            builder.Property(Function(n) n.DisplayName).IsRequired().HasMaxLength(200)
            builder.Property(Function(n) n.HostAddress).IsRequired().HasMaxLength(500)
        End Sub
    End Class

    Public Class InstallationEntityConfig
        Implements IEntityTypeConfiguration(Of InstallationEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of InstallationEntity)) Implements IEntityTypeConfiguration(Of InstallationEntity).Configure
            builder.HasKey(Function(i) i.InstallationId)
            builder.Property(Function(i) i.GameId).IsRequired().HasMaxLength(100)
            builder.Property(Function(i) i.InstallPath).IsRequired().HasMaxLength(1000)
            builder.HasOne(Function(i) i.Node).
                WithMany(Function(n) n.Installations).
                HasForeignKey(Function(i) i.NodeId)
            ' Phase 5h — optional link to a SharedConfigGroup.
            ' IsRequired(False) makes the FK nullable; EF Core's
            ' default OnDelete for optional FKs is ClientSetNull,
            ' so deleting a SharedConfigGroup leaves linked
            ' installations intact with SharedConfigGroupId = NULL
            ' (rather than cascading deletion, which would lose
            ' the installations themselves — dangerous if the
            ' user accidentally deletes a realm). Re-linking
            ' those installations to a different (or new) group
            ' is the recovery path.
            builder.HasOne(Function(i) i.SharedConfigGroup).
                WithMany(Function(g) g.Installations).
                HasForeignKey(Function(i) i.SharedConfigGroupId).
                IsRequired(False)
        End Sub
    End Class

    Public Class InstanceEntityConfig
        Implements IEntityTypeConfiguration(Of InstanceEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of InstanceEntity)) Implements IEntityTypeConfiguration(Of InstanceEntity).Configure
            builder.HasKey(Function(i) i.InstanceId)
            builder.Property(Function(i) i.GameId).IsRequired().HasMaxLength(100)
            ' Phase 1 restart-scheduling fields — cap string widths
            ' so the generated SQLite columns don't end up as
            ' unbounded TEXT. RestartCron holds a cron expression
            ' (5–6 fields, whitespace-separated; 100 chars is plenty),
            ' RestartRuleId holds a GUID-"N" style identifier (32
            ' chars, but keep 100 for consistency with other FK
            ' columns in the schema).
            builder.Property(Function(i) i.RestartCron).HasMaxLength(100)
            builder.Property(Function(i) i.RestartRuleId).HasMaxLength(100)
            ' InstanceSetTag is a free-form user label, max 100
            ' chars to match the other identifier-shaped columns
            ' in the schema. Indexed because the dominant access
            ' pattern at rule-firing time is
            '   WHERE InstanceSetTag = X [AND GameId = Y]
            ' across the whole Instances table — unindexed it'd
            ' be a full scan on every InstanceSet-scoped rule
            ' evaluation.
            builder.Property(Function(i) i.InstanceSetTag).HasMaxLength(100)
            builder.HasIndex(Function(i) i.InstanceSetTag)
            ' SortOrder gets an index so the "list instances in
            ' order within an installation" query doesn't scan
            ' the full table. Composite with InstallationId so
            ' the index is directly usable for the common
            ' WHERE InstallationId = X ORDER BY SortOrder query.
            builder.HasIndex(Function(i) New With {i.InstallationId, i.SortOrder})
            builder.HasOne(Function(i) i.Installation).
                WithMany(Function(inst) inst.Instances).
                HasForeignKey(Function(i) i.InstallationId)
        End Sub
    End Class

    Public Class AutomationRuleEntityConfig
        Implements IEntityTypeConfiguration(Of AutomationRuleEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of AutomationRuleEntity)) Implements IEntityTypeConfiguration(Of AutomationRuleEntity).Configure
            builder.HasKey(Function(r) r.RuleId)
            builder.Property(Function(r) r.RuleName).IsRequired().HasMaxLength(200)
            ' GameFilter holds a GameId ("lastoasis", "factorio")
            ' which matches InstallationEntity.GameId's 100-char
            ' cap. Not indexed — the engine reads all enabled
            ' rules at startup/reload, so per-rule filter is in
            ' memory.
            builder.Property(Function(r) r.GameFilter).HasMaxLength(100)
            ' SortOrder index for the Automation Rules window's
            ' "ORDER BY SortOrder" query. Cheap to maintain since
            ' rules are mutated rarely compared to e.g. instances.
            builder.HasIndex(Function(r) r.SortOrder)
        End Sub
    End Class

    Public Class SteamCredentialEntityConfig
        Implements IEntityTypeConfiguration(Of SteamCredentialEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of SteamCredentialEntity)) Implements IEntityTypeConfiguration(Of SteamCredentialEntity).Configure
            builder.HasKey(Function(c) c.CredentialId)
            builder.Property(Function(c) c.Username).IsRequired().HasMaxLength(200)
        End Sub
    End Class

    Public Class NotificationPluginEntityConfig
        Implements IEntityTypeConfiguration(Of NotificationPluginEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NotificationPluginEntity)) Implements IEntityTypeConfiguration(Of NotificationPluginEntity).Configure
            builder.HasKey(Function(p) p.PluginId)
            builder.Property(Function(p) p.DisplayName).IsRequired().HasMaxLength(200)
        End Sub
    End Class

    Public Class NotificationSubscriptionEntityConfig
        Implements IEntityTypeConfiguration(Of NotificationSubscriptionEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NotificationSubscriptionEntity)) Implements IEntityTypeConfiguration(Of NotificationSubscriptionEntity).Configure
            builder.HasKey(Function(s) s.SubscriptionId)
            builder.Property(Function(s) s.PluginId).IsRequired().HasMaxLength(100)
        End Sub
    End Class

    Public Class RuleExecutionEntityConfig
        Implements IEntityTypeConfiguration(Of RuleExecutionEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of RuleExecutionEntity)) Implements IEntityTypeConfiguration(Of RuleExecutionEntity).Configure
            builder.HasKey(Function(e) e.ExecutionId)
            builder.Property(Function(e) e.RuleId).IsRequired().HasMaxLength(100)
            builder.HasIndex(Function(e) e.RuleId)
            builder.HasIndex(Function(e) e.StartedAtUtc)
        End Sub
    End Class

    Public Class VisibilityProfileEntityConfig
        Implements IEntityTypeConfiguration(Of VisibilityProfileEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of VisibilityProfileEntity)) Implements IEntityTypeConfiguration(Of VisibilityProfileEntity).Configure
            builder.HasKey(Function(e) e.ProfileId)
            builder.Property(Function(e) e.DisplayName).IsRequired().HasMaxLength(100)
        End Sub
    End Class

    Public Class NotificationDestinationEntityConfig
        Implements IEntityTypeConfiguration(Of NotificationDestinationEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NotificationDestinationEntity)) Implements IEntityTypeConfiguration(Of NotificationDestinationEntity).Configure
            builder.HasKey(Function(e) e.DestinationId)
            builder.Property(Function(e) e.DisplayName).IsRequired().HasMaxLength(100)
            builder.Property(Function(e) e.TransportKind).IsRequired().HasMaxLength(40)
            builder.HasIndex(Function(e) e.Enabled)
        End Sub
    End Class

    Public Class DiscordBotConfigEntityConfig
        Implements IEntityTypeConfiguration(Of DiscordBotConfigEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of DiscordBotConfigEntity)) Implements IEntityTypeConfiguration(Of DiscordBotConfigEntity).Configure
            builder.HasKey(Function(e) e.ConfigId)
            ' ConfigId is a stable string ("default" today; future
            ' multi-identity feature would add other values). 50
            ' chars matches the discriminator-shape of similar
            ' columns elsewhere in the schema.
            builder.Property(Function(e) e.ConfigId).HasMaxLength(50)
            builder.Property(Function(e) e.DisplayName).IsRequired().HasMaxLength(100)
        End Sub
    End Class

    Public Class DiscordPanelEntityConfig
        Implements IEntityTypeConfiguration(Of DiscordPanelEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of DiscordPanelEntity)) Implements IEntityTypeConfiguration(Of DiscordPanelEntity).Configure
            builder.HasKey(Function(e) e.PanelId)
            builder.Property(Function(e) e.PanelId).HasMaxLength(50)
            ' Discord snowflake IDs are 64-bit integers serialised
            ' as decimal strings — currently 18-19 digits, with
            ' headroom to about 20. 50 chars is generous future-
            ' proofing without bloating the row.
            builder.Property(Function(e) e.GuildId).IsRequired().HasMaxLength(50)
            builder.Property(Function(e) e.ChannelId).IsRequired().HasMaxLength(50)
            builder.Property(Function(e) e.MessageId).HasMaxLength(50)
            builder.Property(Function(e) e.DisplayName).IsRequired().HasMaxLength(100)
            ' ScopeKind is one of "AllInstances", "Game",
            ' "Installation", "InstanceSet" — short discriminator
            ' string. 40 chars matches NotificationDestination's
            ' TransportKind cap.
            builder.Property(Function(e) e.ScopeKind).IsRequired().HasMaxLength(40)
            ' ScopeTargetId carries either a GameId, InstallationId,
            ' or an InstanceSetTag value depending on ScopeKind. The
            ' largest of those (InstanceSetTag, free-form user
            ' label) is capped at 100 elsewhere; the column here
            ' tolerates a bit more in case of future scope kinds.
            builder.Property(Function(e) e.ScopeTargetId).HasMaxLength(200)
            ' GroupingKind is a short discriminator; same shape as
            ' ScopeKind. "None" / "ByNode" / "ByGame" /
            ' "ByNodeThenGame". 40 chars matches the convention.
            builder.Property(Function(e) e.GroupingKind).IsRequired().HasMaxLength(40)
            ' PanelKind discriminator (Phase 5k): "InstanceManager" /
            ' "PlayerList". Same short-string shape as ScopeKind; the
            ' DB-level default backfills pre-5k rows to the original
            ' kind on migration.
            builder.Property(Function(e) e.PanelKind).
                IsRequired().HasMaxLength(40).HasDefaultValue("InstanceManager")
            ' ShowEmptyGroups (Phase 5k): player-list hide-empty toggle;
            ' DB default False = hide empty instance groups.
            builder.Property(Function(e) e.ShowEmptyGroups).HasDefaultValue(False)
            ' ShowJoinTime / ShowTotalInTitle (Phase 5k-2c): player-list
            ' display toggles; DB default False so pre-5k-2c rows keep
            ' the original look (no join time, count only in footer).
            builder.Property(Function(e) e.ShowJoinTime).HasDefaultValue(False)
            builder.Property(Function(e) e.ShowTotalInTitle).HasDefaultValue(False)
            ' LayoutJson is intentionally uncapped TEXT — it's a
            ' structured JSON document whose size grows with the
            ' element catalogue. Nullable: NULL = use the default
            ' layout in the renderer.
            builder.Property(Function(e) e.LayoutJson)
            ' Index on GuildId so the bot's "list panels in this
            ' guild" lookup (used during interaction routing,
            ' once 5d-2 ships) doesn't full-scan.
            builder.HasIndex(Function(e) e.GuildId)
        End Sub
    End Class

    Public Class DiscordRoleMappingEntityConfig
        Implements IEntityTypeConfiguration(Of DiscordRoleMappingEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of DiscordRoleMappingEntity)) Implements IEntityTypeConfiguration(Of DiscordRoleMappingEntity).Configure
            ' Composite primary key on (GuildId, PanelId, RoleId).
            ' PanelId is empty string "" for guild-default rows
            ' (Phase 5d-5 item 4 added it; rows that pre-existed
            ' the migration default to ""). Discord snowflakes are
            ' globally unique in practice, but the composite key
            ' formalises the "at most one mapping per role per
            ' (guild, scope)" invariant and gives EF a natural
            ' upsert target without a surrogate ID column. EF Core
            ' generates an index that prefixes on GuildId from
            ' this PK definition, which makes the "list mappings
            ' for this guild" lookup (the dominant access pattern —
            ' both at startup-cache load time and when the
            ' configuration UI switches guilds) index-covered
            ' without an explicit secondary index.
            builder.HasKey(Function(e) New With {e.GuildId, e.PanelId, e.RoleId})
            builder.Property(Function(e) e.GuildId).IsRequired().HasMaxLength(50)
            ' PanelId NOT NULL with empty-string default — see
            ' DiscordRoleMappingEntity class summary for the
            ' sentinel rationale. 64 chars matches the panel-ID
            ' shape used elsewhere (8-char hex-ish strings today,
            ' headroom for future formats).
            builder.Property(Function(e) e.PanelId).IsRequired().HasMaxLength(64).HasDefaultValue("")
            builder.Property(Function(e) e.RoleId).IsRequired().HasMaxLength(50)
            ' RoleName is a display snapshot — the matching path
            ' never reads it. 100 chars matches Discord's role
            ' name length limit.
            builder.Property(Function(e) e.RoleName).IsRequired().HasMaxLength(100)
        End Sub
    End Class

    Public Class ChatMessageEntityConfig
        Implements IEntityTypeConfiguration(Of ChatMessageEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of ChatMessageEntity)) Implements IEntityTypeConfiguration(Of ChatMessageEntity).Configure
            builder.HasKey(Function(m) m.MessageId)
            builder.Property(Function(m) m.SessionIdentity).IsRequired().HasMaxLength(200)
            builder.Property(Function(m) m.NodeId).HasMaxLength(100)
            builder.Property(Function(m) m.InstanceId).HasMaxLength(100)
            builder.Property(Function(m) m.DisplayName).HasMaxLength(100)
            ' Platform user IDs: Steam IDs are 17-digit base-10
            ' numbers, Xbox LIVE IDs are similar length; future
            ' platforms (EOS GUIDs, etc.) may run longer. 64 chars
            ' is generous headroom without bloating the row.
            builder.Property(Function(m) m.PlatformUserId).HasMaxLength(64)
            ' CharacterId on Last Oasis is a base-10 number similar
            ' in shape to PlatformUserId. Same 64-char cap for
            ' consistency — cheap, and matches what we'd want for
            ' any future game that surfaces a stable per-character
            ' identifier.
            builder.Property(Function(m) m.CharacterId).HasMaxLength(64)
            builder.Property(Function(m) m.Text).HasMaxLength(4000)
            ' Composite index for the dominant query: "give me chat
            ' for this session, newest first". Covers both the live
            ' chat panel and the retention pruner (which scans by
            ' timestamp range).
            builder.HasIndex(Function(m) New With {m.SessionIdentity, m.TimestampUtc})
            ' Secondary index for retention pruning — the pruner
            ' queries WHERE TimestampUtc < cutoff regardless of
            ' session, so give it a dedicated index.
            builder.HasIndex(Function(m) m.TimestampUtc)
            ' Phase 5g-1: index on CharacterId to support "every
            ' chat line ever spoken by this character across
            ' rename history" queries. NULLs (pre-5g rows + chat
            ' that arrived before identity-resolution completed)
            ' are excluded from the index by SQLite automatically.
            builder.HasIndex(Function(m) m.CharacterId)
        End Sub
    End Class

    Public Class PlayerSessionEntityConfig
        Implements IEntityTypeConfiguration(Of PlayerSessionEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of PlayerSessionEntity)) Implements IEntityTypeConfiguration(Of PlayerSessionEntity).Configure
            builder.HasKey(Function(p) p.PlayerSessionId)
            builder.Property(Function(p) p.SessionIdentity).IsRequired().HasMaxLength(200)
            builder.Property(Function(p) p.PlayerName).IsRequired().HasMaxLength(100)
            builder.Property(Function(p) p.LastHostInstanceId).HasMaxLength(100)
            ' Unique composite key for UPSERT: at most one row per
            ' (session, player). Round C's persistence logic
            ' looks up by this and updates in place.
            builder.HasIndex(Function(p) New With {p.SessionIdentity, p.PlayerName}).IsUnique()
            ' "Who's been here lately" queries sort by LastSeenUtc
            ' — cover that without scanning.
            builder.HasIndex(Function(p) New With {p.SessionIdentity, p.LastSeenUtc})
        End Sub
    End Class

    Public Class SessionHostEntityConfig
        Implements IEntityTypeConfiguration(Of SessionHostEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of SessionHostEntity)) Implements IEntityTypeConfiguration(Of SessionHostEntity).Configure
            builder.HasKey(Function(h) h.HostId)
            builder.Property(Function(h) h.SessionIdentity).IsRequired().HasMaxLength(200)
            builder.Property(Function(h) h.InstanceId).IsRequired().HasMaxLength(100)
            builder.Property(Function(h) h.TileName).HasMaxLength(200)
            builder.HasIndex(Function(h) New With {h.SessionIdentity, h.HostedFromUtc})
            ' Needed by the "close the currently-open row" UPSERT
            ' in Round C — there should be at most one open row
            ' per SessionIdentity at a time, but the query still
            ' needs to find it quickly.
            builder.HasIndex(Function(h) New With {h.InstanceId, h.HostedUntilUtc})
        End Sub
    End Class

    Public Class PlayerActivityEntityConfig
        Implements IEntityTypeConfiguration(Of PlayerActivityEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of PlayerActivityEntity)) Implements IEntityTypeConfiguration(Of PlayerActivityEntity).Configure
            builder.HasKey(Function(a) a.ActivityId)
            builder.Property(Function(a) a.SessionIdentity).IsRequired().HasMaxLength(200)
            builder.Property(Function(a) a.NodeId).HasMaxLength(100)
            builder.Property(Function(a) a.InstanceId).HasMaxLength(100)
            builder.Property(Function(a) a.PlayerName).IsRequired().HasMaxLength(100)
            builder.Property(Function(a) a.EventKind).IsRequired().HasMaxLength(20)
            ' Phase 5g-2 identity columns. Caps match the
            ' ChatMessageEntity equivalents from 5g-1 for
            ' schema consistency: 64 chars for the numeric ID
            ' columns (Steam IDs are 17 digits, Xbox LIVE
            ' similar shape, EOS GUIDs leave headroom), 100
            ' for DisplayName.
            builder.Property(Function(a) a.CharacterId).HasMaxLength(64)
            builder.Property(Function(a) a.PlatformUserId).HasMaxLength(64)
            builder.Property(Function(a) a.DisplayName).HasMaxLength(100)
            ' The dominant query is the History-window timeline:
            ' "give me all activity for session X in time range Y".
            ' Composite index covers it.
            builder.HasIndex(Function(a) New With {a.SessionIdentity, a.TimestampUtc})
            ' For the "filter by player name" query path, where the
            ' caller may not know which session(s) to look in.
            builder.HasIndex(Function(a) New With {a.PlayerName, a.TimestampUtc})
            ' Phase 5g-2: index on CharacterId for "every activity
            ' row this character has ever produced" queries, the
            ' counterpart to the ix_chat_character index 5g-1
            ' added to ChatMessages. NULLs are excluded by SQLite
            ' automatically so pre-migration rows don't bloat it.
            builder.HasIndex(Function(a) a.CharacterId)
        End Sub
    End Class

    Public Class AppSettingEntityConfig
        Implements IEntityTypeConfiguration(Of AppSettingEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of AppSettingEntity)) Implements IEntityTypeConfiguration(Of AppSettingEntity).Configure
            builder.HasKey(Function(s) s.SettingKey)
            builder.Property(Function(s) s.SettingKey).HasMaxLength(100)
            builder.Property(Function(s) s.Value).HasMaxLength(4000)
        End Sub
    End Class

    Public Class SharedConfigGroupEntityConfig
        Implements IEntityTypeConfiguration(Of SharedConfigGroupEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of SharedConfigGroupEntity)) Implements IEntityTypeConfiguration(Of SharedConfigGroupEntity).Configure
            builder.HasKey(Function(g) g.GroupId)
            builder.Property(Function(g) g.PluginId).IsRequired().HasMaxLength(100)
            builder.Property(Function(g) g.GroupType).IsRequired().HasMaxLength(50)
            builder.Property(Function(g) g.DisplayName).IsRequired().HasMaxLength(200)
            builder.Property(Function(g) g.ConfigJson).IsRequired()
            ' Composite index for "list all groups of this type
            ' belonging to this plugin" — the common query when
            ' rendering a plugin-specific management UI ("show
            ' me all LO realms").
            builder.HasIndex(Function(g) New With {g.PluginId, g.GroupType})
        End Sub
    End Class

    Public Class UpdateHistoryEntityConfig
        Implements IEntityTypeConfiguration(Of UpdateHistoryEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of UpdateHistoryEntity)) Implements IEntityTypeConfiguration(Of UpdateHistoryEntity).Configure
            builder.HasKey(Function(e) e.HistoryId)
            builder.Property(Function(e) e.Outcome).IsRequired().HasMaxLength(40)
            builder.Property(Function(e) e.FromVersion).HasMaxLength(100)
            builder.Property(Function(e) e.ToVersion).HasMaxLength(100)
            builder.HasIndex(Function(e) e.AppliedAtUtc)
        End Sub
    End Class

    Public Class PluginSourceEntityConfig
        Implements IEntityTypeConfiguration(Of PluginSourceEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of PluginSourceEntity)) Implements IEntityTypeConfiguration(Of PluginSourceEntity).Configure
            builder.HasKey(Function(e) e.SourceId)
            builder.Property(Function(e) e.DisplayName).IsRequired().HasMaxLength(200)
            builder.Property(Function(e) e.Owner).IsRequired().HasMaxLength(100)
            builder.Property(Function(e) e.Repo).IsRequired().HasMaxLength(100)
            builder.Property(Function(e) e.RepoPath).HasMaxLength(300)
            builder.Property(Function(e) e.Branch).HasMaxLength(100)
            builder.HasIndex(Function(e) New With {e.Owner, e.Repo, e.RepoPath}).IsUnique()
        End Sub
    End Class

    Public Class WebSessionEntityConfig
        Implements IEntityTypeConfiguration(Of WebSessionEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of WebSessionEntity)) Implements IEntityTypeConfiguration(Of WebSessionEntity).Configure
            builder.HasKey(Function(e) e.SessionKey)
            builder.Property(Function(e) e.SessionKey).HasMaxLength(200)
            builder.Property(Function(e) e.EncryptedCookieHeader).IsRequired()
            builder.Property(Function(e) e.CapturedByPluginId).HasMaxLength(100)
        End Sub
    End Class

    ' ============================================================
    '  Design-time factory (for EF migrations)
    ' ============================================================

    ''' <summary>
    ''' Required for EF Core Tools to create migrations.
    ''' If "No DbContext found" error, this is what fixes it.
    ''' </summary>
    Public Class GsmDbContextFactory
        Implements IDesignTimeDbContextFactory(Of GsmDbContext)

        Public Function CreateDbContext(args As String()) As GsmDbContext Implements IDesignTimeDbContextFactory(Of GsmDbContext).CreateDbContext
            Dim options = New DbContextOptionsBuilder(Of GsmDbContext)().
                UseSqlite("Data Source=gsm.db").Options
            Return New GsmDbContext(options)
        End Function
    End Class

    ' ============================================================
    '  Extension methods
    ' ============================================================

    ''' <summary>
    ''' Helper methods for common data operations.
    ''' </summary>
    Public Module GsmDataExtensions

        ''' <summary>
        ''' Creates and configures a GsmDbContext with the default SQLite path.
        ''' </summary>
        Public Function CreateDefaultContext() As GsmDbContext
            Dim options = New DbContextOptionsBuilder(Of GsmDbContext)().
                UseSqlite("Data Source=gsm.db").Options
            Return New GsmDbContext(options)
        End Function

        ''' <summary>
        ''' Creates and configures a GsmDbContext with a custom database path.
        ''' </summary>
        Public Function CreateContext(dbPath As String) As GsmDbContext
            Dim options = New DbContextOptionsBuilder(Of GsmDbContext)().
                UseSqlite($"Data Source={dbPath}").Options
            Return New GsmDbContext(options)
        End Function

        ' ============================================================
        '  AppSettings helpers — typed read/write on the KV table
        '
        '  Centralized here so Round C (retention pruner), Round D
        '  (settings UI) and any future caller all use the same
        '  parsing rules. All misses return the supplied default;
        '  malformed values are treated as misses (not errors) so
        '  a typo in the DB can't crash the app.
        ' ============================================================

        ''' <summary>
        ''' Read a string setting. Returns defaultValue if the key
        ''' is absent.
        ''' </summary>
        <Runtime.CompilerServices.Extension>
        Public Function GetSetting(db As GsmDbContext,
                                    key As String,
                                    defaultValue As String) As String
            Dim row = db.AppSettings.Find(key)
            If row Is Nothing OrElse row.Value Is Nothing Then Return defaultValue
            Return row.Value
        End Function

        ''' <summary>
        ''' Read an integer setting. Returns defaultValue if the key
        ''' is absent or the stored text doesn't parse as Int32.
        ''' </summary>
        <Runtime.CompilerServices.Extension>
        Public Function GetSettingInt(db As GsmDbContext,
                                       key As String,
                                       defaultValue As Integer) As Integer
            Dim row = db.AppSettings.Find(key)
            If row Is Nothing OrElse String.IsNullOrEmpty(row.Value) Then Return defaultValue
            Dim parsed As Integer
            If Integer.TryParse(row.Value, parsed) Then Return parsed
            Return defaultValue
        End Function

        ''' <summary>
        ''' Write a string setting, creating the row if absent.
        ''' Caller is responsible for SaveChanges.
        ''' </summary>
        <Runtime.CompilerServices.Extension>
        Public Sub SetSetting(db As GsmDbContext, key As String, value As String)
            Dim row = db.AppSettings.Find(key)
            If row Is Nothing Then
                db.AppSettings.Add(New AppSettingEntity With {.SettingKey = key, .Value = value})
            Else
                row.Value = value
            End If
        End Sub

        ''' <summary>
        ''' Read a boolean setting, stored as "1"/"0" (the int
        ''' convention GetSettingInt uses). Returns defaultValue if
        ''' the key is absent or unparseable. Phase 5m-1.
        ''' </summary>
        <Runtime.CompilerServices.Extension>
        Public Function GetSettingBool(db As GsmDbContext,
                                       key As String,
                                       defaultValue As Boolean) As Boolean
            Return db.GetSettingInt(key, If(defaultValue, 1, 0)) <> 0
        End Function

        ''' <summary>
        ''' Write a boolean setting as "1"/"0". Caller is responsible
        ''' for SaveChanges. Phase 5m-1.
        ''' </summary>
        <Runtime.CompilerServices.Extension>
        Public Sub SetSettingBool(db As GsmDbContext, key As String, value As Boolean)
            db.SetSetting(key, If(value, "1", "0"))
        End Sub

        ''' <summary>
        ''' Well-known setting keys. Use these instead of string
        ''' literals so typos are compile errors.
        ''' </summary>
        Public Class SettingKeys
            Public Const ChatRetentionDays As String = "ChatRetentionDays"
            ''' <summary>Phase 5m-1 tray preferences (0/1 ints).
            ''' MinimizeToTray hides the window on minimize;
            ''' CloseToTray hides it on the X instead of exiting;
            ''' StartMinimized launches minimized (→ straight to the
            ''' tray when MinimizeToTray is also on).</summary>
            Public Const MinimizeToTray As String = "ui.minimizeToTray"
            Public Const CloseToTray As String = "ui.closeToTray"
            Public Const StartMinimized As String = "ui.startMinimized"
            ''' <summary>Phase 5m-1b — last tree/content splitter width
            ''' in px; restored on next launch.</summary>
            Public Const SplitterDistance As String = "ui.splitterDistance"
            ''' <summary>
            ''' JSON array of TreeNode.Tag values that were expanded
            ''' when the Manager last closed. Restored on next start so
            ''' the user doesn't have to re-expand the same nodes every
            ''' time. Stored as JSON for safe round-tripping of any
            ''' future tag formats.
            ''' </summary>
            Public Const TreeExpandedTags As String = "TreeExpandedTags"
            ''' <summary>
            ''' Phase 5l-1 — self-update. State keys (written by the
            ''' update checker as it polls) and config keys (set in
            ''' Settings). Versions are stored as the raw semver string
            ''' (e.g. "0.4.0" or "0.4.0-rc1"); the tag keeps its
            ''' leading "v". Source is "owner/repo".
            ''' </summary>
            Public Const UpdateLastCheckUtc As String = "update.lastCheckUtc"
            Public Const UpdateLatestVersion As String = "update.latestVersion"
            Public Const UpdateLatestTag As String = "update.latestTag"
            Public Const UpdateReleaseBody As String = "update.releaseBody"
            Public Const UpdateReleaseUrl As String = "update.releaseUrl"
            Public Const UpdateSkippedVersion As String = "update.skippedVersion"
            Public Const UpdateIncludePrereleases As String = "update.includePrereleases"
            Public Const UpdateCheckIntervalHours As String = "update.checkIntervalHours"
            Public Const UpdateSource As String = "update.source"
            ' Phase 5l-2 — version currently downloaded + extracted under
            ' <AppDir>\.updates\{version}\extracted, ready to apply.
            Public Const UpdateStagedVersion As String = "update.stagedVersion"
            ' Phase 5l-3 — running version captured at apply time so the
            ' post-update binary can record from→to in update history.
            Public Const UpdatePendingFromVersion As String = "update.pendingFromVersion"
            ' Phase 6-3 — JSON list of staged (downloaded + validated,
            ' not yet installed) plugins; see PluginStageService.
            Public Const PluginsStaged As String = "plugins.staged"
        End Class

        ''' <summary>
        ''' Default retention in days. Used by the pruner when the
        ''' AppSetting row is absent (first run). Can be overridden
        ''' per-install via the settings UI in Round D.
        ''' </summary>
        Public Const DefaultChatRetentionDays As Integer = 90

        ''' <summary>
        ''' Phase 5l-1 — self-update defaults, used when the AppSetting
        ''' rows are absent. Source is the official "owner/repo";
        ''' check interval is in hours (GitHub's unauthenticated API
        ''' allows 60 req/hr/IP, so 4h — ~6/day — is far under budget).
        ''' </summary>
        Public Const DefaultUpdateSource As String = "siteml/PowerGSM"
        Public Const DefaultUpdateCheckIntervalHours As Integer = 4

        ''' <summary>
        ''' Per-instance AppSettings key: set to "1" once the game has
        ''' generated its IStartupFileProvider config file(s) on the
        ''' node (true from the 2nd launch on for games like Windrose).
        ''' Gates the EditInstanceForm "settings apply from the 2nd
        ''' launch" notice — shown until this flips. Lives in AppSettings
        ''' rather than the instance ConfigJson so an EditInstanceForm
        ''' save (which rewrites ConfigJson from schema fields only)
        ''' can't clobber it, and so it never rides along in
        ''' InstanceConfig.CustomFields.
        ''' </summary>
        Public Function StartupFilesReadyKey(instanceId As String) As String
            Return $"instance.{instanceId}.startupFilesReady"
        End Function

        ' ============================================================
        '  SortOrder helpers
        '
        '  Instance rows within an installation are ordered by
        '  SortOrder ASC. New inserts must pick a SortOrder that
        '  places them at the end of the sibling list; colliding
        '  values are tolerated (ties break on CreatedUtc) but
        '  produce non-deterministic ordering which is a poor UX.
        ' ============================================================

        ''' <summary>
        ''' Returns the next SortOrder value to use when inserting
        ''' a new instance into the given installation. Computes
        ''' max(SortOrder)+1 across existing siblings; returns 1
        ''' for the first instance in an installation. Caller is
        ''' responsible for using this value on the new entity
        ''' BEFORE SaveChanges.
        ''' </summary>
        <Runtime.CompilerServices.Extension>
        Public Function NextSortOrder(db As GsmDbContext,
                                       installationId As String) As Integer
            If String.IsNullOrEmpty(installationId) Then Return 1
            Dim currentMax As Integer = 0
            Try
                ' DefaultIfEmpty(0).Max() avoids the
                ' "Sequence contains no elements" exception when
                ' the installation has zero instances yet — which
                ' happens on the first insert after creating a new
                ' installation.
                currentMax = db.Instances.
                    Where(Function(i) i.InstallationId = installationId).
                    Select(Function(i) i.SortOrder).
                    DefaultIfEmpty(0).
                    Max()
            Catch
                ' On any DB-side failure, fall through to 1. Worst
                ' case the new row gets SortOrder=1 which collides
                ' with an existing one; ties break on CreatedUtc
                ' at display time.
                currentMax = 0
            End Try
            Return currentMax + 1
        End Function

        ''' <summary>
        ''' Like NextSortOrder but for AutomationRules. Computes
        ''' max(SortOrder)+1 across all rules so a freshly created
        ''' rule lands at the end of the existing list. Same
        ''' DefaultIfEmpty(0) safety as the instance variant.
        ''' </summary>
        <Runtime.CompilerServices.Extension>
        Public Function NextRuleSortOrder(db As GsmDbContext) As Integer
            Dim currentMax As Integer = 0
            Try
                currentMax = db.AutomationRules.
                    Select(Function(r) r.SortOrder).
                    DefaultIfEmpty(0).
                    Max()
            Catch
                currentMax = 0
            End Try
            Return currentMax + 1
        End Function

    End Module

End Namespace
