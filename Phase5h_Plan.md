# Phase 5h — Plugin-Defined Shared Config Groups & Source Column

Design document for two related additions: a generalised
"plugin-defined shared configuration group" concept (motivated by
Last Oasis's Realm structure but built so any plugin can opt in)
and a plugin-formatted "Source" column in the History window that
makes use of it. Read this first in the new chat; everything below
assumes the conversation is starting fresh.

---

## Status

- **Phase 5h-1 — Infrastructure.** Shipped May 22, 2026.
  `ISharedConfigProvider` interface in `GSM.Contracts`,
  `SharedConfigGroupEntity` + `InstallationEntity.SharedConfigGroupId`
  FK in `GsmDbContext`, `SharedConfigService` with DPAPI
  encryption-at-rest, DI registration in `ManagerProgram`.
  EF migration `20260522145126_Phase5h_SharedConfigGroups`
  applied.

- **Phase 5h-2 — Merge plumbing.** Shipped May 22, 2026.
  `InstanceManager.MergeConfigLayers` overlays group → install
  → instance config with "empty upper layer doesn't overwrite
  non-empty lower layer" rule. `StartInstanceAsync` and
  `GetMergedCustomFields` both refactored to use it.

- **Phase 5h-3 — LO opt-in.** Shipped May 22, 2026.
  `LastOasisPlugin` implements `ISharedConfigProvider` with
  `CustomerKey` + `ProviderKey` + `RealmName` as the realm
  schema. Install-level schema retains the same three fields
  during transition for backwards-compat.

- **Phase 5h-4 — Management UI.** Shipped May 22, 2026.
  Tools → Shared Resources opens `SharedConfigGroupsForm`
  (TabControl with one tab per provider plugin),
  `SharedConfigGroupEditForm` for per-item create/edit.

- **Phase 5h-5 — Installation editor integration.** Shipped
  May 22, 2026. Realm picker row in both `NewInstallationForm`
  and `EditInstallationForm`, with "New..." button opening
  the group editor inline.

- **Phase 5h-5b — Auto-migration prompt.** SKIPPED. Zero
  deployed copies in the wild, manual migration via the new
  UI takes under a minute for the operator's three
  installations.

- **Phase 5h-6 — Source column.** Shipped May 22, 2026.
  `ISourceLabelProvider` interface + `SourceLabelContext` DTO,
  LO implementation, `HistoryQueryService` dispatch with
  `LoadResolvedInstances` + `ResolveSourceLabel` helpers,
  HistoryWindow column refactor (Tile/Session + Instance →
  Source), session dropdown shows linked realm name via
  extended `FormatSessionLabel`, row tooltip + right-click
  context menu (Copy instance ID / Copy session identity).

---

## Goal

Two concerns surfaced during 5g-2b debugging against the operator's
three-installations-on-one-realm Last Oasis setup:

1. **Credential duplication across same-realm installations.**
   Each LO installation needed its own copy of `CustomerKey` +
   `ProviderKey` in its `InstallationEntity.ConfigJson` even when
   three installs hosted different tile pools on the same realm.
   Rotating the provider key meant editing three installations.
   Adding a fourth instance pool meant copy-pasting credentials
   again. The Realm concept exists in the game's backend (one
   Realm = one CustomerKey on the MyRealm dashboard); the manager
   should reflect that.

2. **History rendering loses realm context.** The session-filter
   dropdown showed `tile — realm {first-8-chars}…` even though
   the operator had set a human-readable realm name elsewhere.
   The "Instance" column showed `Node:Instance:GUID` with no
   indication of which realm the rows belonged to. Operators
   filtering for "show me everything that happened on Site's
   World last week" couldn't easily tell which rows belonged
   to which realm.

The generalisation: both concerns are about per-plugin shared
state that belongs above the installation level. Solution shape
shared with the existing Steam Credentials feature: separate
table, manager-side service, encryption-at-rest for sensitive
fields, UI to manage them, opt-in linkage from installations.

Theme: graduate the "credentials live on the installation" model
to "credentials live on a group above the installation, with the
installation linking into it", while preserving full backwards
compatibility for unlinked installs.

---

## Honest assessment of current infrastructure

The Steam Credentials feature was a useful template — same
shape (separate table, manager-side service, DPAPI encryption,
opt-in linkage from `InstallationEntity.SteamCredentialId`). The
critical difference is plugin-driven schema rather than a fixed
shape: Steam credentials are universally `Name + Username +
Password + Anonymous`, while Realm fields are LO-specific and a
future Cluster / League / Server-Group feature would have its
own fields. The `ISharedConfigProvider` interface formalises that
generality.

The Realm concept never landed as a first-class concept in
earlier phases by deliberate choice — the operator was a single
LO realm operator until early May 2026, at which point
expanding to three installations on the same realm made the
duplication painful enough to motivate the abstraction. April
2026's `RemoveRealmCredentials` migration was an earlier attempt
that got rolled back; this phase finally lands it under a
generalised interface.

For the Source column work: the existing History display had
clean separation (TileDisplayName + InstanceDisplay) but the
formatting was hardcoded in HistoryQueryService and
HistoryWindow rather than delegated to the plugin. The pattern
mirrors how `IGamePlugin.GetLogParseRules` delegates parser
behaviour to plugins — same idea, just for display.

---

## Phase 5h-1: Infrastructure

**Goal:** Define the data model + service surface so subsequent
sub-phases have something concrete to call into.

**Deliverables:**
- `ISharedConfigProvider` interface in `GSM.Contracts/IGamePlugin.vb`
  with four members: `SharedConfigKey`, `SharedConfigLabel`,
  `GetSharedConfigSchema()`, `DiscriminatorFieldKey`.
- `SharedConfigGroupEntity` + `SharedConfigGroupEntityConfig` in
  `GsmDbContext.vb`. Composite `HasIndex` on `(PluginId,
  GroupType)`. `Installations` navigation collection.
- `InstallationEntity.SharedConfigGroupId` nullable string FK
  with `OnDelete=SetNull`. Navigation property to the group.
- `SharedConfigService.vb` in `GSM.Manager/Core/` with
  `ListGroups`, `GetGroup`, `CreateGroup`, `UpdateGroup`,
  `DeleteGroup`, `LoadGroupFieldsPlaintext`. Encryption sentinel
  `__GSM_ENC__:` wrapping base64 DPAPI bytes via
  `CredentialService.ProtectString` / `UnprotectString`.
- DI registration as singleton in `ManagerProgram.vb`.
- EF migration `Phase5h_SharedConfigGroups`. Auto-applied on
  startup via existing `Database.Migrate()` path.

**Design notes:**
- Encryption is per-field, not per-group — operators can have
  groups with mixed sensitive / non-sensitive fields. The
  `IsSensitive=True` flag on each `ConfigFieldDescriptor`
  drives the encryption decision at write time;
  `LoadGroupFieldsPlaintext` decrypts at read time before
  handing values back to the schema renderer.
- `DiscriminatorFieldKey` exists for the migration scenario
  (Phase 5h-5b, dropped) where the manager would have detected
  duplicate values across installs and offered to consolidate.
  Kept on the interface anyway — future tooling that needs to
  identify "which group matches this install's existing config"
  benefits from the plugin telling it which field is the
  canonical identifier.
- `SharedConfigGroupId` on `InstallationEntity` is nullable
  because the link is optional — installs that opt out of the
  group concept continue to store all config in their own
  `ConfigJson`. `OnDelete=SetNull` rather than `Cascade`
  because deleting a Realm should leave installations alive
  (the operator can re-link or fill in install-level values
  as they prefer); cascading would destructively remove
  installations the operator didn't ask to delete.

---

## Phase 5h-2: Three-layer merge

**Goal:** Wire the new group config into the existing config
flow without changing what plugins see.

**Deliverable:** `InstanceManager.MergeConfigLayers(db,
installation, instance)` returning a merged
`Dictionary(Of String, String)`. Three layers in precedence
order (highest wins):

1. Instance-level (`InstanceEntity.ConfigJson`)
2. Installation-level (`InstallationEntity.ConfigJson`)
3. Group-level (`SharedConfigGroupEntity.ConfigJson` via FK,
   decrypted)

**Merge rule:** "blank upper layer doesn't overwrite non-blank
lower layer". A plugin schema that includes `CustomerKey` at
both group and install levels:
- Group has `CustomerKey="abc"`, install has `CustomerKey=""`:
  merge yields `CustomerKey="abc"` (install's blank doesn't
  clobber group's value).
- Group has `CustomerKey="abc"`, install has `CustomerKey="xyz"`:
  merge yields `CustomerKey="xyz"` (explicit override wins).
- Group has `CustomerKey=""`, install has `CustomerKey="xyz"`:
  merge yields `CustomerKey="xyz"`.

**Plugin perspective:** Unchanged. `InstanceConfig.CustomFields`
is still a flat `Dictionary(Of String, String)`; plugins call
`GetFieldString("CustomerKey")` and don't know or care that the
value came from a Realm vs the installation. Layering is purely
transparent to consumers.

**Refactor scope:** `StartInstanceAsync` previously inline-merged
installation + instance dicts; replaced with a call to
`MergeConfigLayers`. `GetMergedCustomFields` (the existing helper
used elsewhere) also refactored to use the same merge function so
all callers see the same precedence.

---

## Phase 5h-3: Last Oasis opt-in

**Goal:** Validate the infrastructure by having the LO plugin
actually use it.

**Deliverable:** `LastOasisPlugin` implements `ISharedConfigProvider`:
- `SharedConfigKey = "realm"`
- `SharedConfigLabel = "Realm"`
- `DiscriminatorFieldKey = "CustomerKey"`
- `GetSharedConfigSchema()` returns three fields:
  `CustomerKey` (required, sensitive), `ProviderKey` (required,
  sensitive), `RealmName` (optional, cosmetic — used in the
  History Source column).

**Transition strategy:** the same three fields stay in
`GetInstallConfigSchema()` during the transition. This means:
- Existing installs continue to work unchanged (their
  install-level CustomerKey/ProviderKey survive intact).
- Linking an existing install to a new Realm doesn't break it
  — the merge favours install over group when both are set, so
  install-level values continue to win until the operator
  manually clears them.
- New installs can either fill install-level (legacy path) or
  link to a Realm (new path) without one breaking the other.

---

## Phase 5h-4: Management UI

**Goal:** Surface the new entity through the existing manager
UI so operators can create and edit groups.

**Deliverables:**
- `SharedConfigGroupsForm` (new, in `RemainingForms.vb`):
  modal dialog opened via Tools → Shared Resources.
  TabControl with one tab per loaded plugin implementing
  `ISharedConfigProvider` — today that's just "Realms" for LO;
  future plugins appear automatically. Each tab contains a
  ListView (Name / Linked installations / Updated columns)
  plus Add / Edit / Delete buttons. Empty state when no
  plugins opt in shows an explanatory message rather than a
  blank dialog.
- `SharedConfigGroupEditForm` (new, in `RemainingForms.vb`):
  per-item editor. DisplayName text input at top + schema panel
  rendered via the existing `SchemaFormBuilder`. Required-field
  validation before save. Exposes `SavedGroupId` property after
  successful save so callers can re-select the just-created
  group.
- `MainForm` Tools menu gets a new "Shared Resources..." entry
  between Steam Credentials and Automation Rules.

**Design notes:**
- Tab labels pluralise the plugin's `SharedConfigLabel` with a
  bare `+"s"` ("Realm" → "Realms"). English-only; irregular
  plurals would need a richer interface.
- Delete confirmation warns when installations are linked
  ("3 installations currently link to this group..."). The FK
  becomes NULL per the migration config rather than cascading,
  so linked installs survive but lose the group's shared
  fields until re-linked.

**VB.NET gotcha encountered:** the first cut used a named
`List(Of (Plugin As IGamePlugin, Provider As ISharedConfigProvider))`
tuple for the per-tab provider list. VB.NET's case-insensitive
identifier resolution kept resolving the loop variable
`gamePlugin` as if it referenced the imported `GSM.Plugin`
namespace, even after renaming the loop variable. Replaced the
named tuple with a small private nested class `ProviderEntry`
with `Game` and `Provider` fields — sidesteps the whole question.
Captured as a reserved-keyword gotcha in `PowerGSM_Reference.md`.

---

## Phase 5h-5: Installation editor integration

**Goal:** Let operators link installations to groups directly
from the install editor flows.

**Deliverables:**
- `EditInstallationForm` gets a new "Realm:" row between Steam
  Account and Run _CommonRedist. Hidden by default;
  `LoadExistingValues` makes it visible when the installation's
  plugin implements `ISharedConfigProvider`. Pre-selects the
  current `SharedConfigGroupId` via `PopulateRealmCombo`. Save
  writes the new selection (NULL for "(none)").
- `NewInstallationForm` gets the same row after Steam credentials.
  Visibility refreshes on game-selection change via a new
  `RefreshRealmPicker` helper called from `OnGameChanged`. Save
  sets `SharedConfigGroupId` on the new entity if the user
  picked something.
- Both forms get a "New..." button next to the picker that
  opens `SharedConfigGroupEditForm` in create-new mode. On save,
  the picker refreshes and selects the new group via
  `dlg.SavedGroupId`.
- Form heights bumped: `EditInstallationForm` 640 → 680,
  `NewInstallationForm` 740 → 775.

---

## Phase 5h-5b: Auto-migration prompt — SKIPPED

**Original goal:** Detect existing installations sharing a
`CustomerKey` value, offer to consolidate them into a single
Realm + clear the now-redundant install-level fields.

**Reason dropped:** Reviewed with operator on May 22, 2026.
Zero deployed copies in the wild — the only installation set
that needs migration is the operator's own three LO installs.
Manual migration through the 5h-5 UI: create one Realm, link
three installs, optionally blank out install-level
CustomerKey/ProviderKey. Total time under a minute.

The detection logic was the easy part; the prompt UX
(per-discriminator-group dialog, per-install opt-out, status
report after) was the bulk of the work. Not worth it for the
single operator who can do it by hand.

---

## Phase 5h-6: Source column

**Goal:** Use the new realm linkage to give History rows a
meaningful "where did this come from" label that includes the
human-readable realm name where applicable.

**Deliverables:**
- `ISourceLabelProvider` interface + `SourceLabelContext` DTO
  in `GSM.Contracts/IGamePlugin.vb`. Single method:
  `FormatSourceLabel(context As SourceLabelContext) As String`.
  Context fields: `SessionIdentity`, `TileName`, `NodeName`,
  `InstallationName`, `InstanceName`, `InstanceId`,
  `SharedConfigGroupName`.
- `LastOasisPlugin` implements `ISourceLabelProvider`. Format:
  `{TileName} — {RealmDisplay} — {Node}/{Install}`, dropping
  empty segments. RealmDisplay prefers
  `context.SharedConfigGroupName` (the linked group's
  DisplayName) and falls back to
  `realm {first-8-of-realm_id}…` parsed from
  `context.SessionIdentity` — matching pre-5h-6
  `FormatSessionLabel` output for unlinked installs.
- `HistoryQueryService` refactor:
  - New `TimelineRow.SourceLabel` + `SnapshotRow.SourceLabel`
    + `SnapshotRow.InstanceId` properties.
  - New `ResolvedInstance` private nested class capturing
    per-InstanceId NodeName / InstallationName / InstanceName /
    GameId / SharedConfigGroupName.
  - New `LoadResolvedInstances(distinctIds)` — two queries
    (Instance + Installation + Node, then SharedConfigGroup
    DisplayNames) merged into a per-InstanceId map.
  - New `ResolveSourceLabel(sessionIdentity, instanceId,
    contexts, tileNames, registry)` static helper —
    builds context, dispatches to plugin via
    `PluginRegistry.GetPlugin(GameId)` + `TryCast` to
    `ISourceLabelProvider`, catches plugin exceptions, falls
    back to `BuildDefaultSourceLabel` ("Node/Install/Instance",
    skipping empty segments) when needed.
  - Snapshot replay tuple expanded to capture `InstanceId`
    from the join event; SnapshotRow gets the full source
    treatment that timeline rows do.
- `HistoryWindow` UI refactor:
  - `BuildTimelineColumns` drops "Tile / Session" + "Instance",
    adds "Source" (width 540).
  - `BuildSnapshotColumns` renames "Tile / Session" to "Source"
    (width 400).
  - `RenderTimeline` + `RenderSnapshot` read `SourceLabel`
    instead of `TileDisplayName` + `InstanceDisplay`.
  - `ListView.ShowItemToolTips = True` + per-row
    `ToolTipText` showing full SessionIdentity + InstanceId.
  - New `ContextMenuStrip` on the result list with "Copy
    instance ID" + "Copy session identity" items. Opening
    handler disables items whose identifier is empty on the
    selected row. `Clipboard.SetText` wrapped in try/catch
    for the rare "another app is holding the clipboard" case.
- Session dropdown fix-up (added mid-phase per operator
  feedback): `LoadKnownSessions` gets a new pre-pass joining
  SessionHosts → Instance → Installation → SharedConfigGroup
  for a session-identity → realm-DisplayName map.
  `FormatSessionLabel` gains an optional `realmDisplayName`
  parameter and uses it in place of the truncated realm_id
  substring when present. Unlinked installs continue to show
  the legacy `realm {hash}` format.

**Why Source replaces both old columns rather than augmenting
one:** the old "Tile / Session" column was tile-level; the old
"Instance" column was infrastructure-level. The Source column
combines both perspectives into a single "where this happened"
identifier, which is how operators actually consume the data
("I'm looking at a chat row — where did it come from"). Two
columns forced the eye to combine them; the single Source column
does the combination at format time and includes the realm name
that previously wasn't surfaced anywhere in the row data.

**InstanceId visibility:** previously embedded as the last
segment of the old Instance column (`Node:Instance:GUID`) for
grep-the-log workflows. Source column drops that visibility
deliberately — the GUID is long and ugly in the cell. Tooltip
+ right-click context menu give the operator the same data on
demand without cluttering the cell.

**Backwards-compat on TimelineRow/SnapshotRow:** the legacy
`TileDisplayName` and `InstanceDisplay` properties are still
written by HistoryQueryService and still on the row classes —
just no longer rendered by HistoryWindow. Kept around in case
downstream code (Discord panels, automation rules) reads them;
removable in a future cleanup once nothing else references them.

---

## Validation

Live-tested against the operator's three-installation LO setup:
- Three Realms can be created via Tools → Shared Resources
  (or one, for the single-realm case).
- Each installation links via Edit Installation → Realm picker.
- The Source column shows `{Tile} — Site's World —
  node/install` for linked installs once the operator clears
  install-level CustomerKey/ProviderKey (until cleared, the
  merge keeps the install-layer values winning per precedence).
- Session dropdown reflects the realm name for linked installs.
- Tooltip + right-click copy actions work; status bar confirms
  the clipboard write.
- UTC toggle re-renders correctly (cache replay produces
  matching SourceLabel + tooltip + Tag).
- Snapshot mode also gets the new Source format.
