# PowerGSM Reference — Plugins

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
How PowerGSM's runtime plugins work: per-file Roslyn compilation, plugin
version reporting, the source / manifest / update distribution model,
plugin-defined shared config groups, and the Phase 7 utility-plugin surface.
The build-order view of where plugins sit in the solution is in
[`build-and-project.md`](build-and-project.md); plugin-related VB.NET pitfalls
(namespace shadowing, the `<RequiresContracts>` marker rules) live in
[`vbnet-gotchas.md`](vbnet-gotchas.md).

---

### Per-file plugin compilation

`PluginRegistry.ReloadAll` compiles each `.vb` file in the Plugins
directory as its own `VisualBasicCompilation` with a unique assembly
name (`GSM.Plugins.<filename>`). All plugin assemblies still share one
`AssemblyLoadContext` so unload/reload cycles work atomically. A single
plugin file failing to compile does NOT prevent others from loading —
failures are recorded per-file in the `PluginReloadSummary`.

---

### Plugin-reported installed version (`IVersionAwarePlugin.GetInstalledVersionAsync`)

For non-SteamCmd installs, the manager-side `BuildVersionStamp`
used to produce "installed (timestamp)" / "download (timestamp)"
placeholders that could never match the canonical version
string `GetLatestVersionAsync` returned ("2.0.76"). The
`VersionCheckService`'s inequality check then reported drift on
every poll, putting a permanent "update available" badge on
fresh installs.

`IVersionAwarePlugin.GetInstalledVersionAsync(config, client,
cancellation)` is the fix — the plugin reads its installed
version off the node's filesystem in the same format
`GetLatestVersionAsync` returns. The plugin uses the supplied
`INodeClient` to call the existing file-ops endpoints (no
direct filesystem access required); `allowedRoots` /
`allowedExtensions` scope the read to just the version-bearing
file. Returns `Nothing` on any failure (file missing, parse
failure, network blip) so the caller falls back to the
synthetic stamp rather than recording a meaningless value.

Called by `InstallationManager` post-install/update on
non-SteamCmd installs (Steam installs continue to use the
appmanifest ACF buildid path), and opportunistically by
`VersionCheckService` on every poll cycle so pre-existing rows
with placeholder stamps upgrade themselves without requiring a
reinstall.

Factorio implements the method by reading `data/base/info.json`
(the manifest of Factorio's bundled `base` mod, which the engine
updates to match its own version on every patch). Last Oasis
doesn't implement `IVersionAwarePlugin` at all — the contract
change is non-breaking for plugins that don't opt in.

---

### Plugin sources + manifests + updates (Phase 6)

Plugins are managed artifacts: identified by an inline manifest,
fetched from GitHub-backed sources, staged before touching the live
folder, and hot-reloaded on install — no Manager restart, ever (the
big contrast with the 5l-3 binary swap).

**Manifest** (`PluginManifestParser`, Core) — a single comment line
`' <plugin id="..." name="..." version="..." author="..."
requiresContracts="N">` plus an optional `' <dependencies>` block of
`<depends id min />`. Parsed pre-compile (regex, never throws); the
legacy `' <RequiresContracts: N>` comment still works as a fallback
for the contracts version and is being phased out slowly, not
removed. A file with no `<plugin>` block loads exactly as before —
an untracked "local" plugin (no update tracking, "—" columns).
`author` is **pure credit**: free-text, displayed, never used for
trust/identity/origin (it's self-declared and spoofable). Official-
ness is a property of the *source* a plugin came from, so a
community-authored plugin accepted into the official repo is
Official origin while crediting its real author.
`PluginRegistry.GetManifest(gameId)` exposes the parsed manifest per
loaded plugin; manifests clear on every reload.

**Sources** (`PluginSourceEntity`, migration `PluginSources`;
`PluginCatalogService`, Core) — GitHub repos browsable for plugins.
The official source (`siteml/PowerGSM` @ `GSM.PluginsSource`,
master) is seeded idempotently at startup after `Migrate()`, is
un-deletable (only disable), and `IsOfficial` is never settable from
the CRUD path — users can't mint a privileged source. Catalog fetch
= one contents-API listing per source (rate-limited: 60/hr
unauthenticated, mitigated by a per-session cache keyed on SourceId)
+ per-file raw.githubusercontent.com fetches (not rate-limited) +
manifest parse. Only files declaring a `<plugin>` block are
catalogued, so helper `.vb` files in a repo are ignored. Catalogs
read the *pushed* branch — local uncommitted manifest edits don't
appear until pushed.

**Stage** (`PluginStageService`, Core) — download to
`<install>\.plugin-updates\{id}\`, then validate *there*: size guard
(error-page detection), authoritative re-parse of the downloaded
text (id must match the catalog entry), blocking dependency
resolution (Decision 7: loaded plugins, then same-source + official
catalogs; missing/too-old/available-but-not-installed each block
with a named message), and naming warnings (Decision 2: third-party
plugins are expected to use `{sourceOwner}_`-prefixed ids — derived
from the SOURCE owner, not the manifest; bare ids and collisions
with loaded plugins warn-and-confirm, never silently shadow).
Staged state persists as JSON in the `plugins.staged` settings key.
`Plugins\` is never written at stage time, so a mid-download restart
can only leave a partial file in staging. NB: the result class is
`PluginStageResult` — `StageResult` was already taken by
`UpdateOrchestrator` in the same namespace.

**Install / update / uninstall** — install = `InstallStaged`
(copy-then-discard into `PluginRegistry.PluginsDirectory`) +
`ReloadAll` + orphan-banner refresh. Update detection
(`CheckForUpdatesAsync`) compares loaded manifest versions against
the best per-id version across all enabled sources via
`SemanticVersion`; unversioned/local plugins simply aren't tracked;
nothing ever auto-applies. Uninstall is file-level in the Plugin
Status file section (works on enabled or disabled files, deletes
outright after an orphan-consequence consent; data/config kept).

**UI — Tools → Manage Plugins** (`ManagePluginsForm`) — one tabbed
window (Status / Sources / Updates) that HOSTS the three existing
forms via `TopLevel = False` + `FormBorderStyle.None` + `Dock =
Fill`, rather than merging their code. Two embedding gotchas worth
remembering: (1) a hosted form closing itself should close the
shell (`FormClosed` handler), and (2) **`DialogResult` buttons only
auto-close forms shown via `ShowDialog`** — on hosted (modeless)
forms they go dead, so the shell recursively rewires any
`DialogResult`-carrying button's Click to close the shell. Tab
content loads lazily (each form's `Load` fires on first view), so
the Updates check only runs when its tab is opened. MainForm
refreshes the orphan warning once, when the shell closes. All three
list surfaces (catalog, updates, plugin files) use **checkbox batch
selection**: `ListView.CheckBoxes = True`, actions read
`CheckedItems` (never `SelectedItems`), buttons are count-labelled,
and a "Select all" CheckBox syncs two-way with item checks via a
`_suppressCheckEvents` reentrancy guard (set the flag, mutate, clear
in Finally) — without the guard, programmatic check-all loops
re-enter `ItemChecked` per row. Repopulating a list never fires
`ItemChecked`, so every populate path must explicitly reset the
select-all box + button state. Batch flows are stage-all → one
combined consent (per-item warnings inlined) → act-all → **reload
once** → one summary.

**WinForms layout gotcha (re-learned here):** controls added to
`Controls` earlier sit *higher* in z-order — a wide Label added
before overlapping Buttons paints over them. Keep bottom-strip
labels short of the buttons' x-range (`AutoEllipsis` for overflow).

---

### Plugin-defined shared config groups (Phase 5h-1 through 5h-5)

Motivated by the operator running three Last Oasis installations
on a single realm with different tile pools: each install
needed its own copy of `CustomerKey` + `ProviderKey` in its
`InstallationEntity.ConfigJson`, and rotating credentials
required editing three installations. The generalisation is
plugin-driven — LO's Realm concept is one instance of a
broader "shared config above the installation level" pattern
any future plugin can opt into (Cluster for an Ark cluster
setup, League for a competitive Factorio league, etc.).

**Interface contract.** `ISharedConfigProvider` in
`GSM.Contracts/IGamePlugin.vb` is the plugin's opt-in surface:

- `SharedConfigKey As String` — lowercase identifier for the
  group type (e.g. `"realm"`). Used as `GroupType` on the
  storage row.
- `SharedConfigLabel As String` — user-facing singular name
  (e.g. `"Realm"`). The management UI pluralises with a bare
  `+"s"` to label its tabs.
- `GetSharedConfigSchema() As IReadOnlyList(Of ConfigFieldDescriptor)`
  — the field shape, same descriptor type used by
  `GetInstallConfigSchema()` and `GetInstanceConfigSchema()`.
  Sensitive fields use `IsSensitive=True` to opt into
  encryption-at-rest.
- `DiscriminatorFieldKey As String` — the field key whose
  value identifies the group across installs (e.g.
  `"CustomerKey"`). Currently used by no consumer; would have
  driven the dropped 5h-5b auto-migration prompt and is kept
  for any future tooling that needs to identify "which group
  matches this install's config".

**Storage.** `SharedConfigGroupEntity` (in
`GSM.Manager/Data/GsmDbContext.vb`) is a plain row table with
`GroupId` (GUID-string PK), `PluginId` (game id of the
declaring plugin), `GroupType` (the plugin's
`SharedConfigKey`), `DisplayName` (user-set), `ConfigJson`
(serialised field dict with sensitive fields wrapped per the
encryption sentinel — see below), `CreatedUtc`, `UpdatedUtc`.
Composite `HasIndex` on `(PluginId, GroupType)` so the manager
can efficiently list all groups for a given plugin's shared
config type. `InstallationEntity` gains a nullable
`SharedConfigGroupId` FK with `OnDelete=SetNull` — deleting a
group leaves its installations intact, just unlinked. Migration:
`20260522145126_Phase5h_SharedConfigGroups`, auto-applied at
Manager startup via the existing `Database.Migrate()` path.

**Encryption-at-rest.** `SharedConfigService` (in
`GSM.Manager/Core/SharedConfigService.vb`) owns the
CRUD surface (`ListGroups`, `GetGroup`, `CreateGroup`,
`UpdateGroup`, `DeleteGroup`) plus `LoadGroupFieldsPlaintext`.
Field-level encryption: when writing, each field marked
`IsSensitive=True` in the plugin's schema gets wrapped with a
sentinel prefix `__GSM_ENC__:` followed by base64 DPAPI bytes
(via `CredentialService.ProtectString`). On read,
`LoadGroupFieldsPlaintext` detects the sentinel and decrypts
via `UnprotectString` before handing values back to the schema
renderer. Same DPAPI mechanism as the existing
Steam-credentials flow; the sentinel approach (rather than a
sibling encrypted column) keeps the storage shape uniform and
the encryption decision schema-driven.

**Three-layer merge.** New
`InstanceManager.MergeConfigLayers(db, installation, instance)`
overlays three layers in precedence order (highest wins):
layer 0 = group (decrypted via SharedConfigService), layer 1
= installation (`InstallationEntity.ConfigJson`), layer 2 =
instance (`InstanceEntity.ConfigJson`). The transition
discipline is **"empty upper layer doesn't clobber non-empty
lower layer"** at each overlay step — critical for
backwards-compat with the LO transition where the same
field keys (`CustomerKey`, `ProviderKey`) live at both group
and install levels during the migration period. An install
with non-empty install-level CustomerKey + a linked Realm
with CustomerKey on the group: the install value wins (operator
hasn't migrated yet). The same install with install-level
CustomerKey blanked: the group value wins (operator has
migrated). Layer 0 is skipped entirely when the plugin
doesn't implement `ISharedConfigProvider` OR the installation
has `SharedConfigGroupId = NULL`; load errors are logged and
treated as "no group layer". Plugins see the merged result via
`InstanceConfig.CustomFields` exactly as before; the layering
is transparent to consumer code.

**LO opt-in.** `LastOasisPlugin` implements
`ISharedConfigProvider` with three fields: `CustomerKey`
(required, sensitive), `ProviderKey` (required, sensitive),
`RealmName` (optional, cosmetic — used in the History Source
column). Schema rendering uses `FieldType.Text` (visible) for
all three; encryption is purely a storage concern and the
operator can still read their credentials in the editor for
verification. The same three fields stay in
`GetInstallConfigSchema()` during the transition for
backwards-compat; existing installs continue to work
unchanged until the operator manually links and clears.

**Management UI.** Tools → Shared Resources opens
`SharedConfigGroupsForm` (in `RemainingForms.vb`). TabControl
with one tab per loaded plugin implementing
`ISharedConfigProvider`; empty state when no plugins opt in.
Each tab contains a ListView (Name / Linked installations /
Updated columns) plus Add / Edit / Delete buttons. The
Delete button warns when installations are linked (FK becomes
NULL, not a cascade) so the operator knows what happens.
Per-item editor is `SharedConfigGroupEditForm`, which renders
the plugin's schema via the existing `SchemaFormBuilder` and
exposes `SavedGroupId` after a successful save so calling
forms can re-select the just-created group in a dropdown.

**Installation editor integration.** Both `NewInstallationForm`
and `EditInstallationForm` gained a "Realm:" row between the
Steam Account and Run _CommonRedist rows. The row is hidden
until the selected plugin implements `ISharedConfigProvider`
(NewInstallationForm's `OnGameChanged` calls `RefreshRealmPicker`;
EditInstallationForm reads the installation's plugin once on
load). A ComboBox lists `(none)` + all existing groups for the
plugin; the "New..." button opens `SharedConfigGroupEditForm`
in create-new mode and re-selects the new group on return
via `dlg.SavedGroupId`. Save writes the selection to
`InstallationEntity.SharedConfigGroupId` (NULL for `(none)`).

**Skipped scope.** Phase 5h-5b (auto-migration prompt) was
reviewed and dropped. The detection logic ("find installations
sharing a DiscriminatorFieldKey value, offer to consolidate")
was straightforward; the UX (per-group dialog, per-install
opt-out, status report) was the bulk of the work, and with
zero deployed copies in the wild plus a sub-minute manual
migration path through 5h-5, not worth shipping.

**Operator workflow for migrating the LO setup.** (1) Tools
→ Shared Resources → Realms tab → Add, name it "Site's World",
paste CustomerKey + ProviderKey, optionally set RealmName,
Save. (2) For each of the three existing LO installs: Edit
Installation → Realm picker → select "Site's World" → Save.
(3) Optional, to start using the realm-layer values rather
than the install-layer copies: re-Edit each installation,
blank out install-level CustomerKey + ProviderKey, Save.
Until step 3, the merge keeps install-level values winning
per precedence — functional but redundant.

**VB.NET gotcha encountered.** First cut of
`SharedConfigGroupsForm.PopulateTabs` used a named
`List(Of (Plugin As IGamePlugin, Provider As ISharedConfigProvider))`
tuple for the per-tab provider list. With `Imports GSM.Plugin`
active in the file (needed for the interface types), VB.NET's
case-insensitive identifier resolution treated bare references
to a same-scope loop variable as if they referenced the
imported `GSM.Plugin` namespace — producing BC30112
("'GSM.Plugin' is a namespace and cannot be used as an
expression"). Renaming the loop variable alone wasn't
sufficient; the named tuple element `Plugin` participated in
the same case-insensitive shadow. Final fix: replace the
named tuple with a small private nested class
(`ProviderEntry { Game, Provider }`) and use a short
non-conflicting loop variable name (`gp`). The reserved-keyword
table below has the row.

---

### Plugin-defined Source column for History (Phase 5h-6)

Motivated by two observations during 5h-5 testing:

1. The History window's "Tile / Session" column showed the
   truncated realm_id substring even when the installation
   was linked to a Realm with a human-readable DisplayName.
2. The "Instance" column showed `Node:Instance:GUID` with no
   indication of which realm the rows belonged to, making
   cross-realm filtering visually noisy.

Fix shape: merge the two columns into a single "Source"
column whose content is plugin-formatted, and move the raw
InstanceId (previously embedded in the Instance column
for grep-the-log workflows) into a hover tooltip + right-
click action.

**Interface contract.** `ISourceLabelProvider` in
`GSM.Contracts/IGamePlugin.vb` is the plugin's opt-in:

- `FormatSourceLabel(context As SourceLabelContext) As String`
  — invoked once per row at render time. Should be cheap
  (no I/O, no expensive lookups). Returning Nothing or empty
  falls back to the manager-supplied default, so a plugin
  that opts in but bails out under some condition gets a
  sensible default rather than a blank cell.

**Context shape.** `SourceLabelContext` (also in
`IGamePlugin.vb`) carries everything the plugin might need
for labelling without exposing EF or storage internals:

- `SessionIdentity` — raw, game-defined (e.g.
  `"lastoasis:{realm_id}:{tile_id}"`); Nothing for games
  without a session concept.
- `TileName` — friendly tile name observed via parse rules
  (e.g. `"[N5][PvE] Ikronic Pain"`); empty when not yet known.
- `NodeName`, `InstallationName`, `InstanceName` — display
  names of the host node, installation, and instance.
- `InstanceId` — full GUID. Plugins typically don't render
  this in the label (the UI exposes it via tooltip and
  right-click); available for plugins that want a short
  prefix.
- `SharedConfigGroupName` — the user-set DisplayName of the
  linked SharedConfigGroup, Nothing if not linked. Plugins
  should prefer this over digging `RealmName`-like fields
  out of merged config because the user picked it as their
  friendly label.

**LO implementation.** Three em-dash-separated segments
(`{TileName} — {RealmDisplay} — {Node}/{Install}`), dropping
any segment with no data. RealmDisplay prefers
`context.SharedConfigGroupName` and falls back to
`"realm {first-8-of-realm_id}…"` parsed out of
`SessionIdentity` — matching pre-5h-6 `FormatSessionLabel`
output for unlinked installs so the visual experience for
unlinked rows is unchanged. The instance-path segment is
intentionally Node/Install (NOT Node/Install/Instance)
because the LO backend reassigns tiles across instances
within an installation freely; the on-disk installation is
the meaningful disambiguator at the History level. The full
InstanceId is reachable via the row tooltip and right-click
"Copy instance ID" action for log-grep workflows.

**Manager dispatch.**
`HistoryQueryService.LoadResolvedInstances` does a two-query
pre-pass:

1. Inner join Instance + Installation + Node for all
   distinct InstanceIds in the result set, projecting
   NodeName + InstallationName + InstanceName + GameId +
   `install.SharedConfigGroupId`.
2. For installs whose `SharedConfigGroupId` is non-null,
   pull the SharedConfigGroup DisplayName in a single query
   (LEFT JOIN expressed as a second query + in-memory merge
   since typical N is tiny).

The result is a `Dictionary(Of String, ResolvedInstance)` (a
private nested class) keyed by InstanceId. Per row,
`ResolveSourceLabel` builds a `SourceLabelContext`, looks up
the plugin via `PluginRegistry.GetPlugin(GameId)`, casts to
`ISourceLabelProvider` if available, and dispatches — catching
plugin exceptions defensively (a misbehaving plugin's
formatting bug shouldn't kill the whole query). Plugins not
opting in OR returning Nothing/empty get a manager-supplied
default: `BuildDefaultSourceLabel` produces
`"Node/Install/Instance"`, skipping empty segments, falling
back to the raw SessionIdentity if nothing resolves.

The same machinery runs for both `TimelineRow` and
`SnapshotRow`. `SnapshotRow` previously didn't carry
`InstanceId`; added during this phase, captured from the
join event during activity replay.

**Session dropdown fix-up.** Added late in the phase after
the user noticed that the session-filter ComboBox at the top
of the History window still showed the truncated realm_id
substring even after the Source column had been switched to
realm DisplayName. Root cause: `LoadKnownSessions` builds
`SessionSummary.DisplayLabel` via `FormatSessionLabel`, which
only knew about tile name + parsed realm_id from
SessionIdentity. Fix: new pre-pass in `LoadKnownSessions`
joins SessionHosts → Instance → Installation →
SharedConfigGroup to build a `session-identity → realm-
DisplayName` map (first-write-wins per identity);
`FormatSessionLabel` gained an optional `realmDisplayName`
parameter and uses it in place of the truncated realm_id
substring when present. Unlinked installs continue to render
`tile — realm {hash}` as before; session-host rows pre-dating
the realm link stay on the legacy format until the session
is hosted again under the new linkage (no backfill).

**HistoryWindow column changes.** `BuildTimelineColumns`
dropped "Tile / Session" (260 px) + "Instance" (280 px) and
added "Source" (540 px, the merged width).
`BuildSnapshotColumns` renamed "Tile / Session" to "Source"
(400 px). The renderers read `r.SourceLabel` instead of
`r.TileDisplayName` + `r.InstanceDisplay`; the legacy
properties are kept on the row classes for backwards-compat
but no longer rendered.

**Row tooltip + right-click context menu.** Per-row
`ToolTipText` shows multi-line `"Session: {full identity}\n
Instance: {full GUID}"`, skipping either line when empty.
`ListView.ShowItemToolTips = True` enables the WinForms
built-in row tooltip rather than a separately-managed
`ToolTip` control — simpler, and the tooltip text just
updates per render. `ListViewItem.Tag` carries the
underlying `TimelineRow` / `SnapshotRow` so the context
menu actions can read SessionIdentity / InstanceId from the
row object via a small `ExtractRowIdentifiers` helper.

`ContextMenuStrip` has two items: "Copy &instance ID" and
"Copy &session identity" — accelerator keys I and S. The
`Opening` handler reads the selected row's identifiers via
`ExtractRowIdentifiers` and enables/disables each item
based on whether the corresponding identifier is non-empty,
so accidental no-op clicks can't happen. The copy actions
use `Clipboard.SetText` wrapped in `Try/Catch` (clipboard
can be transiently locked by another process), and confirm
via the status bar (`"Copied instance ID: 0a1b2c3d..."`).
Tooltip + Tag are set fresh on every render call —
including the UTC-toggle cache replay — so both stay
consistent with what's actually displayed.

---

### Utility plugins (Phase 7)

A second plugin kind on the Manager. Game plugins (`IGamePlugin`)
manage installations/instances; **utility plugins**
(`IUtilityPlugin`, namespace `GSM.Utility`) don't — they react to
Manager-wide events and act through a capability-gated context.
Key architectural facts worth keeping:

- **Same pipeline, two extra rules.** Utility plugins flow through
  the Phase 6 acquire → stage → consent → install → hot-reload
  path unchanged. The registry enforces two things game plugins
  don't: a `<plugin>` manifest with id+version is REQUIRED (no
  legacy/manifest-less leniency — utility plugins are new), and
  `IUtilityPlugin.PluginId` must match the manifest id. Ids share
  ONE keyspace with game plugins, so a cross-kind id collision is
  refused. `_utilityPlugins` is a sibling dict to `_plugins` in
  PluginRegistry, cleared and repopulated on every reload.

- **ContractsVersion 1 → 2.** The first bump. VERSIONING.md was
  amended with a documented exception: a bump is warranted for a
  major NEW plugin-facing surface (not just breaking changes) so a
  plugin that requires it fails fast on an older Manager with one
  clear message instead of a Roslyn "type not defined" cascade.
  Purely additive — contracts-v1 game plugins load unchanged;
  utility plugins must declare `requiresContracts="2"`.

- **Host + dispatch.** `UtilityPluginHost` (Core, DI singleton)
  subscribes to a new `PluginRegistry.Reloaded` event (raised at
  the end of every `ReloadAll`, inside the reload lock — handlers
  must offload, so the host restarts its plugins via `Task.Run`).
  It MUST be resolved from DI before the first `ReloadAll` at
  startup, or the lazy singleton never constructs, never
  subscribes, and utility plugins load-but-never-initialise. Each
  plugin gets a bounded `Channel(Of UtilityEvent)` (256,
  DropOldest) drained on a background task; `HandleEventAsync`
  exceptions are counted and the plugin is SUSPENDED after 5
  consecutive failures (shown in Plugin Status), reinstated on the
  next reload. Events tap `NotificationEmitter.Emitted` — which
  today only carries PlayerName on join/leave and has no
  chat/server-state entry points, so ChatMessage/ServerStateChange
  and the identity fields are deferred to 7-4a.

- **Capabilities are consent, NOT a sandbox.** Plugins are
  full-trust compiled code. The manifest `requires="..."` list and
  the gated `UtilityContextImpl` (undeclared access throws a named
  error) are an informed-consent + convenience-API mechanism. The
  real defenses remain provenance + readable source + never-auto.
  This is stated explicitly so nobody later mistakes the list for
  containment.

- **7-3b static ratchet (two cheap gates that ARE real).**
  (a) *Reference-set gating*: a capability-declaring plugin
  without `network` is compiled against a `System.Net.*`-stripped
  reference set (`StripNetworkReferences` filters
  `ReferenceAssemblies.Net80` by leaf name), so undeclared network
  use is a COMPILE error. Scoped to plugins that opted into the
  capability model — game plugins (no `requires`) keep every
  reference, so their network use is untouched. Computed lazily
  per-file. (b) *Syntax audit*: `PluginSourceAudit.Scan` walks the
  parsed tree at stage time for DllImport / Process.Start /
  reflection / undeclared-network, surfacing advisory lines in the
  install+update consent. Advisory only; reflection-by-string and
  obfuscation are explicitly out of scope (only an out-of-process
  host could catch those, and that's deliberately unplanned).

- **Web-session capture (Decision 7a).**
  `IUtilityContext.CaptureWebSessionAsync(startUrl,
  completionUrlPattern, cookieDomain)` shows a Manager-owned
  **WebView2** dialog (`Microsoft.Web.WebView2`, Manager-only
  NuGet — plugins never reference it). The user performs a real
  third-party login; on reaching the completion URL the cookies
  for the domain are harvested via
  `CoreWebView2.CookieManager.GetCookiesAsync` — which reads the
  browser cookie jar directly and so captures **HttpOnly** cookies
  (the decisive advantage over JS injection, since session cookies
  are typically HttpOnly). Runs on a dedicated STA thread with a
  modal `ShowDialog` pump, so it's safe to call from any thread
  (including a plugin's drain loop) and never blocks the Manager
  UI. Requires the WebView2 Evergreen Runtime — absent → a clear
  "runtime missing" result, not a crash. Browser state lives in a
  wipeable per-plugin `WebView2Data\{pluginId}` folder.

- **Event tap — identity-rich events (7-4a).** The 7-2 events were
  re-sourced. PlayerJoin/PlayerLeave now come off the tail of
  `InstanceManager.PersistPlayerObservationAsync` (where the full
  identity cascade is already assembled) instead of
  `NotificationEmitter.Emitted` (which only carried a decorated
  display label); ChatMessage comes from `MirrorChatForInstanceAsync`
  (~5s cursor-deduped); ServerStateChange from
  `HandleTileLoaded`/`HandleTileUnloaded` (LO tile bind/unbind only
  — Conan/Factorio never fire it). So a `UtilityEvent` now carries
  resolved `CharacterId` / `PlatformUserId` / `Platform` /
  `CharacterName` + the instance's `SessionIdentity`
  (`lastoasis:{realm}:{tile}` on LO; `{gameId}:{instanceId}`
  fallback elsewhere). `UtilityEvent` gained `SessionIdentity` +
  `CharacterName` (additive — NO ContractsVersion bump); `PlayerName`
  now carries the RAW persona, resolved name in `CharacterName`.
  Behaviour change: synthetic leaves (stop-flush, downtime-
  reconcile) now reach utility plugins (correct for programmatic
  consumers; the emitter had suppressed them only to keep Discord
  quiet). Game plugins are source-unchanged — only Manager plumbing
  moved.

- **lo-myrealm reference plugin (7-4b).** First first-party utility
  plugin (`GSM.PluginsSource\LoMyrealmPlugin.vb`, id `lo-myrealm`).
  It is a name-resolution helper, NOT a SteamID fetcher — the
  CharacterId↔SteamID64↔persona↔display-name chain is already
  assembled by the LO parse rules + IdentityResolver; myrealm's
  distinct value is the AUTHORITATIVE current character name read
  from the rename page
  (`/realm/{realm_id}/Characters/{character_id}/Rename`; realm_id
  off the event's SessionIdentity, scope = everything after the
  first colon). Contributes CharacterId → CharacterName back
  through the resolver, filling the naming window before the first
  Persisting tick. **VerifyOnJoin** (default on) re-reads on JOIN
  to catch portal renames (never prompts; ≥5 min/character, 30 min
  after failures). A one-shot **"Sign in at next plugin reload"**
  config flag triggers login manually (the auto-prompt only fires
  on a genuine naming gap, which never happens on a fully-known
  realm). Expiry handled structurally (no-redirect GET; 3xx or
  served sign-in page → invalidate + notify once → next gap
  re-prompts), so the unknown session lifetime is moot. Two
  findings worth keeping: the capture completion pattern must be
  `/customer/` started at the site root — `WebSessionCaptureForm`'s
  completion check matches ANY navigation including the START URL,
  so a `myrealm.lastoasis.gg` pattern completes instantly on the
  sign-in page with anonymous cookies; and the Name-input scrape is
  attribute-order-tolerant (id-or-name = "Name", then value).

- **Shared web-session store (7-5).** Session capture/persist/expiry
  lives in the MANAGER, not the plugin — the host already owns the
  capture dialog, and a broker *plugin* is the wrong shape (plugins
  are isolated; no plugin→plugin provision exists). Two additive
  `IUtilityContext` members gated by `web-capture`:
  `GetOrCaptureWebSessionAsync(sessionKey, startUrl,
  completionUrlPattern, cookieDomain, allowPrompt)` and
  `InvalidateWebSession(sessionKey)`. `WebSessionStore` (Core, DI
  singleton) = in-memory cache → `web_sessions` table (migration
  `AddWebSessions`), cookie headers **DPAPI-encrypted at rest** via
  `CredentialService.ProtectString`/`UnprotectString` (retires
  7-4b's plaintext-in-config cookie). Keyed `"{site}:{account}"`
  (e.g. `myrealm:default`). The store owns once-per-key prompt
  throttling + in-flight dedup: concurrent callers for one key
  await ONE dialog; a cancelled/failed capture prompt-blocks the
  key until `Invalidate` or restart; a DPAPI decrypt failure is
  treated as absent so a fresh capture self-heals. Plugins sharing
  a key share the session — THAT is the cross-plugin provision,
  zero plugin coupling. NO ContractsVersion bump: additive members
  on an existing interface whose only consumer (lo-myrealm) ships
  with them — "routine new member", not the "new plugin-facing
  kind" that justified the 7-1 bump.

- **Web Sessions UI + liveness (7-5b).** `WebSessionsForm` = a
  fourth hosted tab in `ManagePluginsForm` (`TabWebSessions = 3`).
  Lists key / captured-by / captured / last-used (NEVER the
  cookie) via `WebSessionStore.ListSessions()` → `WebSessionInfo`;
  **Revoke** = `Invalidate` (also the orphan-cleanup path when the
  owning plugin was uninstalled); **Validate** = real liveness.
  Validation is an OPT-IN side-interface `IWebSessionValidator`
  (`CanValidateWebSession` prefix-claim + `ValidateWebSessionAsync`
  → Valid/Expired/Failed + detail) — additive, same pattern as
  ILogParser/IModManager; adding it to `IUtilityPlugin` itself
  would fail-compile every existing plugin (VB has no default
  interface members). The host routes via
  `UtilityPluginHost.ValidateSessionAsync`/`HasValidatorFor`, using
  `WebSessionStore.PeekHeader` (read-only — NEVER captures).
  Validators run OUTSIDE the event queue, so they must be
  thread-safe + classify-only (no invalidate/notify; the UI offers
  the revoke on Expired). lo-myrealm validates by probing the
  realm's `General/UpdateName` page (exists for the life of the
  realm), and when no realm has been learned from gameplay yet,
  **discovers one from the portal**: GET the authenticated landing
  page → harvest every `/customer/{id}` link (owned + admin'd) →
  first customer page's `/realm/{id}` wins (persisted as
  `myrealm.realmId`). A customer with no realm configured yet is
  Valid ("signed in; no realm configured yet") via a `SawCustomers`
  flag — NOT a failure. On success the realm name is read back and
  surfaced as `realm "…" reachable`. `DiscoverRealmIdAsync` +
  `GetPageAsync` + `TryReadRealmNameAsync` are the reusable seed of
  the Phase 7-6 realm-onboarding scrape.

- **myrealm realm onboarding & import (7-6).** Turns the 7-5b
  discovery seed into a full import. `IWebPortalDataProvider` (opt-in
  side-interface in `GSM.Utility`, same pattern as
  `IWebSessionValidator`): `CanProvideForSession` +
  `DiscoverRecordsAsync(requestedKey, allowPrompt, context)` →
  `IReadOnlyList(Of WebPortalImportRecord)`. lo-myrealm implements it
  by self-capturing its own session (ignores `requestedKey` — see the
  shadowing gotcha row below — and always uses its `SessionKey`
  constant; `allowPrompt` lets onboarding's no-session-yet case open
  the login dialog, and a landing-page redirect triggers an
  invalidate-and-reprompt for a stale stored session), then walking
  the authenticated landing → every `/customer/{id}` → its
  `/customer/{id}/Providers` page, emitting ONE record per
  (CustomerKey, ProviderKey) pair. Each `WebPortalImportRecord`
  carries `GameId` + `SharedConfigKey` (its target — `lastoasis` /
  `realm`), the plaintext `Fields`, `MatchFieldKeys` (identity fields
  for dedup — lo-myrealm sets `{CustomerKey, ProviderKey}`), a
  plugin-composed `SuggestedDisplayName`, and `UsedBy` (the provider
  label). The host exposes `HasAnyPortalProvider()` (button
  visibility) + `DiscoverAllPortalRecordsAsync(allowPrompt)`
  (iterates every hosted `IWebPortalDataProvider`, passing `Nothing`
  as the session key = "your default"). `PortalImportService` (Core,
  DI singleton) is the GENERIC upsert: `ComputeImportPlan(db,
  records)` fetches the target plugin's `ISharedConfigProvider`
  schema, lists its existing groups, and matches each record on ALL
  `MatchFieldKeys` (decrypted plaintext via `LoadGroupFieldsPlaintext`,
  Ordinal) → `PortalImportPlanItem` classified CreateNew / Update /
  Unchanged; `ApplyImportPlan` runs `CreateGroup`/`UpdateGroup`. It
  never hard-codes a game field name. UI: an **Import…** button per
  tab in `SharedConfigGroupsForm` → `RunImportAsync` (discover →
  filter to this tab's GameId/SharedConfigKey → compute plan) →
  `PortalImportForm` (checked ListView; CreateNew/Update pre-checked,
  Unchanged greyed/inert) → apply → refresh. **Per-provider-key
  model:** a realm hosted from N providers becomes N groups sharing
  RealmName but differing by ProviderKey — no list-typed schema.
  **History consistency:** `SourceLabelContext` gained
  `SharedConfigFields` (the linked group's NON-sensitive fields only
  — sentinel-prefixed encrypted values dropped, so the keys are
  never decrypted on the label path), loaded via a new
  `SharedConfigService.LoadNonSensitiveFields`. LO's
  `FormatSourceLabel` reads `SharedConfigFields("RealmName")` first,
  so the History Source column shows the canonical realm name while
  the group DisplayName keeps its per-provider suffix. All additive;
  no ContractsVersion bump. **Update (7-7):** in-memory multi-session
  routing (decision 2) shipped in 7-7; only the *persisted*
  session→realm access map remains deferred — realm ADMINISTRATION
  (writes) is Phase 10, and persisting the map is what those writes
  justify.

### Stardew Valley plugin (0.5.0, plugin v0.1.0)

`stardewvalley` — headless dedicated Stardew via SMAPI + the
`siteml/SMAPIDedicatedServerMod` fork (separate repo; consumed as a
GitHub release zip because the game DLLs can't live in the PowerGSM
solution). `MaxInstancesPerInstallation = 1`; game port hardcoded
UDP 24642, declared via a locked `IsPort` field so the allocator
counts it (Tier-4 fork Harmony patch is the future unlock for both).

Interfaces implemented: `ILaunchOptionsProvider` (StdoutIsLog;
env vars — Windows `GALLIUM_DRIVER=llvmpipe`, Linux
`LIBGL_ALWAYS_SOFTWARE=1` + `DISPLAY=:97`), `IStartupFileProvider`
(round-trip render of `Mods/DedicatedServer/config.json` from the
merged instance config on every start; unknown/hand-added fields
survive; `PasswordProtected` gates set-if-absent),
`IManagedDirectoriesProvider` + `IFileGenerationProvider` (Farm
Backups dir + Archive/Restore Saves operation — saves live under the
OS user profile outside the install root, so archives via
tar/unzip bridge them into a managed dir for cross-node migration;
single-farm scope via tar's trailing member arg),
`IPrerequisiteProvider` (`linux-xvfb`, `linux-unzip`).

Platform launch shapes:

- **Windows**: exe candidate `StardewModdingAPI.exe`; GPU-less nodes
  get Mesa llvmpipe DLLs from the install step (ExtractOnlyPaths
  pulls just the two DLLs from the ~1 GB mesa 7z).
- **Linux**: exe `/bin/sh`, args
  `-c "[ -e /tmp/.X97-lock ] || Xvfb :97 ... & sleep 1; exec ./StardewModdingAPI"`.
  xvfb-run was rejected: it's a wrapper script, so the spawned pid
  was the script and graceful SIGINT never reached SMAPI (observed:
  stop didn't stop). The sh bootstrap `exec`s SMAPI so the spawned
  pid IS SMAPI; the Xvfb daemon is shared per node on display :97
  and deliberately outlives the game (X lock file = idempotence).

Install chain: credentialed SteamCMD (appid 413150; no dedicated
depot, account must own the game) → SMAPI installer zip
(`ExtractToRelativePath="gsm-smapi-installer"`,
`StripTopLevelDirectory`, run with `RequiresRealConsole=True`
because the installer calls Console.Clear/ReadKey; Linux adds a
chmod +x step — zip extraction drops the exec bit — and builds all
paths with forward slashes by hand, since Path.Combine runs on the
Windows Manager) → server-mod zip into `Mods\` → (Windows +
SoftwareRendering) mesa DLLs.

Parse rules ride the fork's structured `[PGSM]` lines
(READY/JOIN/LEAVE/CHAT/DAY/INVITECODE); the ServerBot
"(ip) has joined" line is an addr-only PlayerJoin (RemoteAddress
capture only) so EventStore's PendingRemoteAddress stash hands the
IP to the immediately-following real JOIN. Day rollover feeds
MatchState with the raw season/day/year triple, which the Manager's
Discord panel context case parses into "Farm — spring 2, year 1".

Saves location (why Farm Backups exists): Windows service =
`C:\Windows\System32\config\systemprofile\AppData\Roaming\StardewValley\Saves`;
Linux = `~/.config/StardewValley/Saves` of the node user. Restore
dispatches by extension on Linux (`unzip -o` or python3 zipfile for
.zip, `tar -xf` otherwise); Windows' bundled bsdtar reads both.
