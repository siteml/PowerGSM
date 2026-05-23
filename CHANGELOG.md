# Changelog

All notable changes to PowerGSM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to pre-1.0 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
as documented in [VERSIONING.md](VERSIONING.md): `MINOR` bumps may break
compatibility with the previous version, `PATCH` bumps do not.

## [Unreleased]

## [0.3.0] - 2026-05-22

### Changed

- **InstancePanel player columns renamed for cross-game
  accuracy.** "Steam name" → "Platform name",
  "Steam/Platform ID" → "Platform ID". The slot is
  game-dependent (Steam handle on LO, Funcom FLS handle
  on Conan, multiplayer username on Factorio); the
  Steam-prefixed label was misleading on Conan in
  particular post-5g-2b. Underlying data binding
  unchanged — the column still reads
  `PlayerSession.PlatformPersona`. Closes the
  "Conan InstancePanel Steam name column label" Backlog
  item.

- **NodePanel status label switched to attach/detach
  vocabulary.** "Enabled" / "Disabled" → "Attached" /
  "Detached". Companion to the new attach/detach toggle
  (see Added) — "attach" implies state preservation,
  which matches what the flag does, while "enable"
  carried a binary-functional connotation that doesn't.
  Underlying database column stays `IsEnabled` to avoid
  a column-rename migration; the vocabulary change is
  UI-only.

- **History window "Source" column replaces "Tile / Session" +
  "Instance" (Phase 5h-6).** The two old columns are consolidated
  into a single Source column whose contents are plugin-formatted
  via the new `ISourceLabelProvider` interface (see Added). Last
  Oasis renders `{TileName} — {RealmName} — {Node}/{Install}`,
  dropping any segment with no data; plugins not implementing the
  interface get a manager-supplied default of
  `{Node}/{Install}/{Instance}`. The instance-path segment is
  intentionally Node/Install rather than Node/Install/Instance
  for LO because the LO backend reassigns tiles across instances
  within an installation — the on-disk installation is the
  meaningful disambiguator at the History level. The full
  InstanceId GUID (previously embedded as the last segment of
  the old Instance column for grep-the-log workflows) is now
  reachable via the new row tooltip and right-click context
  menu. Snapshot-mode rows get the same plugin-formatted label;
  `SnapshotRow` gained `InstanceId` (captured from the join
  event during activity replay) for that purpose. The legacy
  `TimelineRow.TileDisplayName` + `TimelineRow.InstanceDisplay`
  properties are kept on the row for backwards-compat but no
  longer rendered.

- **History row tooltip + right-click context menu (Phase 5h-6).**
  Hovering any row pops a tooltip with the full SessionIdentity
  and full InstanceId on separate lines (skipping either line
  when empty). Right-click opens a context menu with two items:
  "Copy instance ID" and "Copy session identity" — both copy
  the raw value to the clipboard and confirm via the status bar.
  The Opening handler disables items whose identifier is empty
  on the selected row, so accidental no-op clicks can't happen.
  Tooltip + `ListViewItem.Tag` are set fresh on every render call,
  including the UTC-toggle cache replay, so both stay in sync
  with what's actually displayed.

- **`FormatSessionLabel` learns about linked realms (Phase 5h-6).**
  The session-filter dropdown at the top of the History window
  now shows the linked SharedConfigGroup's DisplayName
  ("Forested Wetlands — Site's World") instead of the truncated
  realm_id substring for installs that link to a group. A new
  pre-pass in `LoadKnownSessions` walks SessionHosts → Instance
  → Installation → SharedConfigGroup and feeds the realm name
  through to the formatter via a new optional `realmDisplayName`
  parameter; first-write-wins per identity if (somehow) multiple
  installs hosting the same session link to different groups.
  Unlinked installs continue to render `tile — realm {hash}` as
  before. Session-host rows pre-dating the realm link stay on
  the legacy format until that session is hosted again under
  the new linkage — no backfill.

- **Conan parse-rule "Map Loaded" classifier corrected.** The
  Conan Exiles plugin's `LogWorld: Bringing World` parse rule
  was set to `ParsedEventKind.Custom` capturing `MapPath`, which
  was a silent no-op — `Custom` is a scrape-only kind that
  populates `ServerStateResponse.CustomFields` but doesn't
  affect `ServerState.CurrentMapPath`. Changed to
  `ParsedEventKind.TileLoaded` matching the equivalent LO rule
  so the map path now correctly populates the server-state
  tracking + mirrors to the persistent `instance_state` row.

- **`PlayerSession` identity model split.** `PlayerSession.Name`
  is gone, replaced by `PlatformPersona` (Steam handle / Xbox
  gamertag — captured from the Login URL's `?Name=` parameter,
  known immediately on join) and `DisplayName` (in-game
  character name — captured from the LO `Persisting` line and
  from chat speakers, known after the player's first chat or
  Persisting tick). On Last Oasis these can diverge whenever
  a player renames their character via myrealm; without the
  split, the in-game-renamed character appeared as their
  original Steam persona everywhere the manager rendered
  them. Manager UI surfaces (player list, Discord panels,
  slash command output) default to `DisplayName ?? PlatformPersona`
  via a coalesce, so a player who hasn't been name-resolved
  yet still renders their Steam handle rather than going blank.
  Factorio is unaffected by the divergence: PlatformPersona
  stays Nothing because the Factorio multiplayer username is
  both platform identity and in-game display name in one
  field, which lands on DisplayName.

- **`ChatMessage` identity expansion.** `ChatMessage.PlayerName`
  is gone, replaced by `DisplayName` (always populated, carries
  whatever name the chat line emitted) plus `PlatformUserId`
  and `CharacterId` (populated when the speaker's session has
  been identity-resolved at the time the chat line fires;
  Nothing otherwise). Enables cross-rename queries against
  chat history — a `WHERE CharacterId = X` query returns every
  line that character ever spoke, regardless of what name they
  were going by at the time.

- **`ChatMessageEntity` migration on the Manager.** The
  `ChatMessages` SQLite table renames `PlayerName` →
  `DisplayName` and adds `PlatformUserId` + `CharacterId`
  columns plus an index on `CharacterId`. EF Core 8
  auto-detected the property rename and emitted `RenameColumn`
  in the generated migration, so existing chat history
  survives intact under the new column name. New identity
  columns are NULL on pre-5g-1 rows.

- **`PlayerActivity` identity snapshot columns.** The
  `PlayerActivity` SQLite table gains `CharacterId`,
  `PlatformUserId`, and `DisplayName` columns plus a
  non-unique index on `CharacterId`. Populated at write
  time by a new `InstanceManager.PersistPlayerObservationAsync`
  that wire-calls the Node's `/players` endpoint and
  matches the joining/leaving player against the resolved
  session by `PlatformPersona` or `DisplayName`. The
  History window's Join/Leave rows now render the in-game
  character name (matching how Chat rows have always
  rendered), closing the activity-vs-chat asymmetry that
  was a known gap throughout 5g-1. Misses — Node hasn't
  resolved the session yet at join time, or has already
  removed the session by the time the leave-side wire
  call resolves — leave the columns NULL; the renderer
  falls back to the raw `PlayerName` via the new
  `IdentityFormatter`. Pre-migration rows continue to
  render under that same fallback. The originally-planned
  one-shot backfill from `ChatMessages` for pre-5g-2 rows
  was dropped during scoping: on Last Oasis,
  `PlayerActivity.PlayerName` (Steam handle) and
  `ChatMessages.DisplayName` (character name) differ by
  default for nearly every player from character creation
  onward, so name-equality matching across the two
  tables would only recover the edge case of players who
  happened to pick their Steam handle as their character
  name, at non-trivial false-positive risk on busier
  tiles. Architectural choice was write-time snapshot
  (rather than render-time lookups or a Manager-side
  mirror of the Node's `players` table); snapshot
  semantics now match `ChatMessageEntity`'s existing
  approach. Migration: `Add-Migration
  Phase5g2_PlayerActivity_Identity`, then
  `Update-Database`.

- **Shared `IdentityFormatter` helper.** New
  `GSM.Manager.Core.IdentityFormatter` module with one
  method: `Format(displayName, platformPersona,
  fallback)` returning the first non-empty value. Three
  consumers now share it instead of duplicating inline
  coalesces: `HistoryQueryService.LoadTimeline`'s
  activity-row assembly, `GsmSlashCommands.BuildPlayersResponse`
  (Discord `/players`), and the InstancePanel player
  list. 5g-1 testing surfaced subtle rendering
  differences between consumers from inline duplication;
  this centralises the coalesce decision in one place
  so future visibility-profile gating (admin vs guest
  views, PlatformUserId redaction) has a single edit
  point.

- **Conan plugin parse-rule labelling corrected (5g-2b).**
  The Conan Exiles plugin's `LogNet: Join succeeded:` and
  `LogNet: Player disconnected:` parse rules now capture
  the post-colon token into the `PlatformPersona` group
  rather than `DisplayName`. The token is structurally
  the FLS handle (Funcom's account-level identifier —
  sometimes bare like `losno420`, sometimes with a
  discriminator like `losno420#72569`, depending on how
  the account was provisioned), NOT the in-game character
  name. Character names only arrive via chat lines and
  via the `ConanSandbox: Display: Character ID <n> has
  name <Name>` spawn line (the latter not consumed yet —
  see Backlog Phase 5g-2c). The original labelling
  polluted the Node's `PlayerSession.DisplayName` with
  platform-identity data until chat eventually landed
  and overwrote it, and produced History join/leave rows
  showing the FLS handle for characters whose chat rows
  correctly rendered as the character name. The leave-
  side rename also closes a latent bug where the leave
  event's FLS-handle token would no longer match the
  session via the `DisplayName` key after chat had
  flipped the session's DisplayName to the character
  name — matching by `PlatformPersona` is stable across
  the chat-driven DisplayName updates. Living Conan
  sessions bound under the old rules need to disconnect
  and reconnect once for the new binding to take effect;
  old History rows stay on the FLS handle permanently
  (no backfill, same false-positive-risk rationale as
  the 5g-2 backfill drop above).

- **History viewer render-time chat fallback for activity
  rows (5g-2b).** New
  `HistoryQueryService.ApplyChatFallbackDisplayNames`
  helper backstops the write-time identity snapshot
  introduced by the `PlayerActivity` migration. For
  Join/Leave TimelineRows whose snapshot DisplayName was
  empty or equal to the raw PlayerName, AND
  PlatformUserId is populated, the helper looks up the
  most recent `ChatMessages.DisplayName` for that
  (SessionIdentity, PlatformUserId) pair and overrides
  `TimelineRow.PlayerName` with the result. One indexed
  query per distinct (sid, pid) pair, leveraging the
  `IX_chat_pid` index from 5g-1. Handles the edge cases
  where the write-time snapshot couldn't bind a character
  name — first-time-on-this-Node players (Node's
  players-table cache misses), cross-Node migrations
  where a returning player joins on a Node that doesn't
  have them in its persistent cache, and (most
  importantly) Conan join events that fire before chat
  lands. Players who never chatted within the queried
  scope still fall through to the raw parser PlayerName —
  best-effort backstop, not a complete resolution path.
  Closing the remaining never-chatted gap for Conan would
  require parsing the Character ID spawn line plus a new
  EventStore stash path for `(cid + display, no pid)`
  events; deferred to Backlog Phase 5g-2c.

### Added

- **Phase 5g-2c — Conan Character ID binding for silent
  players.** New `Character Spawn (Character ID →
  CharacterId + DisplayName)` parse rule on the Conan
  plugin captures the spawn line
  (`ConanSandbox: Display: Character ID <n> has name <X>
  and guild ID <g>.`) that fires ~100-200ms after Join
  succeeded. Classified as `PlayerIdentity`.

  **EventStore handling.** The spawn line carries
  CharacterId + DisplayName but no PlatformUserId / IP /
  PlatformPersona, so it can't match an existing session
  by any key. New `TryBindRecentSpawn` helper applies a
  temporal heuristic: among active sessions with no
  CharacterId yet, find the one joined within the last
  3 seconds. If exactly one matches, bind cid+display
  directly. If zero or multiple match (concurrent joins),
  fall back to a cid-keyed stash in the existing
  `PendingIdentitiesByCharacterId` collection.

  **Drain extension.** `DrainPendingCidIdentity` now also
  applies DisplayName from pending entries, guarded with
  "only when the session's DisplayName is empty or equals
  PlatformPersona" so a chat-bound DisplayName isn't
  displaced by a stale spawn entry. The ChatMessage
  handler now calls `DrainPendingCidIdentity` after
  `ApplyFields` so chatty-but-late-bound players also
  drain cleanly.

  **Result.** First-time-ever players who join and never
  chat now render their in-game character name in the
  History window for the typical low-population server
  case. Known limitation documented in both
  `ConanExilesPlugin.GetLogParseRules` and
  `EventStore.PlayerIdentity`: busy-server scenarios with
  concurrent joins where the temporal heuristic is
  ambiguous AND no chat fires for one of the matching
  sessions — those rows still render as the FLS handle.
  Bounded by concurrent join rate; not visible on the
  operator's typical setup.

  Closes the "Phase 5g-2c — Conan Character ID line
  binding for silent players" Backlog item.

- **Node attach/detach toggle.** New context-menu item
  on each node in the tree ("Detach Node" when attached,
  "Attach Node" when detached) flips
  `NodeEntity.IsEnabled`. Detached nodes are filtered out
  of `InstanceManager.FetchAllInstanceIds` (the
  background polling loop's per-instance status refresh)
  and `VersionCheckService.RunOnePassAsync` (background
  version polling), removing the 3-second retry spam
  when a remote node is offline. The node's existing
  configuration is preserved — re-attaching resumes
  polling on the next iteration (within 3 seconds). The
  tree visually marks detached nodes with grey text + a
  "[detached]" suffix; the NodePanel status label
  follows suit (see Changed).

  **Out of scope (deliberately):** existing log streams
  to a detached node are NOT actively cancelled — they
  continue until the underlying TCP connection drops or
  the operator closes the viewer. Explicit operator
  actions (manual Start/Stop/Restart from the
  InstancePanel, opening a log viewer, manual "Check for
  Updates") are also NOT gated; only the background
  polling loops are. Operator wanted background-noise
  suppression, not an entire node-disable wall.

  **No schema migration needed.** Repurposes the
  existing vestigial `NodeEntity.IsEnabled` column,
  which had been declared with no UI to toggle it and no
  poll site to read it from. Only consumers prior to
  this change were `NewInstallationForm`'s node dropdown
  filter (still works correctly under the new
  semantics) and the `NodePanel` status label
  (re-vocabularised).

  Closes the "attach/detach toggle" piece of the
  "Node attach/detach + config import/export/merge/split"
  Backlog item; export / import / merge / split remain
  pending.

- **Plugin-defined shared configuration groups
  (Phase 5h-1 through 5h-5).** Plugins can opt into a new
  shared-config concept where multiple installations link to a
  common group whose fields they share via a three-layer merge.
  The Last Oasis plugin uses it for **Realms**: a single Realm
  holds the realm-wide `CustomerKey` + `ProviderKey` +
  `RealmName`, and the operator setup of three LO installs
  hosting different tile pools on the same realm previously
  required duplicating credentials into each install's
  `ConfigJson`. With the feature, the credentials live on the
  Realm and each install just links to it.

  **Interface** (Phase 5h-1, `GSM.Contracts`): new
  `ISharedConfigProvider` interface. Plugins declare
  `SharedConfigKey` (lowercase id), `SharedConfigLabel`
  (user-facing string — "Realm" for LO),
  `GetSharedConfigSchema()` returning
  `IReadOnlyList(Of ConfigFieldDescriptor)`, and
  `DiscriminatorFieldKey` (the field whose value identifies the
  group across installations — `CustomerKey` for LO).

  **Storage** (Phase 5h-1): new `SharedConfigGroupEntity` table
  with `GroupId` PK, `PluginId`, `GroupType`, `DisplayName`,
  `ConfigJson`, `CreatedUtc`, `UpdatedUtc`. `InstallationEntity`
  gains a nullable `SharedConfigGroupId` FK with
  `OnDelete=SetNull` — deleting a group leaves its installations
  alive but unlinked, falling back to install-level config.
  Migration: `20260522145126_Phase5h_SharedConfigGroups`.

  **Service** (Phase 5h-1): new `SharedConfigService` owns CRUD
  with field-level encryption-at-rest via DPAPI (same mechanism
  as `CredentialService`). Fields marked `IsSensitive=True` in
  the plugin schema get a `__GSM_ENC__:` sentinel prefix
  wrapping base64 DPAPI bytes; `LoadGroupFieldsPlaintext`
  decrypts at read time before handing values back to the
  schema renderer.

  **Three-layer merge** (Phase 5h-2): new
  `InstanceManager.MergeConfigLayers(db, installation, instance)`
  overlays group → install → instance with the rule "blank
  upper-layer values don't overwrite non-blank lower-layer
  values". So a Realm's `CustomerKey` flows through to instance
  config unless an install explicitly overrides it, and an
  install's value flows through unless the instance explicitly
  overrides. Plugins see the merged result via
  `InstanceConfig.CustomFields` exactly as before — layering is
  transparent to them.

  **LO opt-in** (Phase 5h-3): `LastOasisPlugin` implements
  `ISharedConfigProvider`, exposing `CustomerKey` + `ProviderKey`
  + `RealmName` as the realm schema. The same three fields
  remain in `GetInstallConfigSchema()` during the transition for
  backwards-compat — the merge favours install over group when
  both are set, so existing installs keep working unchanged
  until the operator manually links them and (optionally)
  clears the install-level values.

  **Management UI** (Phase 5h-4): new Tools → Shared Resources
  dialog. `SharedConfigGroupsForm` has one tab per loaded plugin
  implementing `ISharedConfigProvider` (today: just "Realms" for
  LO; future plugins appear automatically). Each tab lists
  existing groups with linked-installation counts plus
  Add/Edit/Delete buttons. `SharedConfigGroupEditForm` renders
  the plugin's schema via the existing `SchemaFormBuilder`, so
  password fields / integer pickers / file pickers all behave
  consistently with the install / instance editors. Delete
  warns when installations are linked (FK becomes NULL per the
  migration config, not a cascade).

  **Installation editor integration** (Phase 5h-5): both
  `NewInstallationForm` and `EditInstallationForm` gained a
  Realm row containing a ComboBox + "New..." button. The row
  hides automatically when the selected plugin doesn't
  implement `ISharedConfigProvider`. NewInstallationForm's
  `OnGameChanged` refreshes visibility + contents when the user
  picks a different game; EditInstallationForm pre-selects the
  installation's current `SharedConfigGroupId` on load. The
  "New..." button opens `SharedConfigGroupEditForm` in
  create-new mode and re-selects the new group on return via
  the form's `SavedGroupId` property.

  **Scope dropped:** auto-migration prompt for existing
  installations sharing a `CustomerKey`. Reviewed and dropped —
  zero deployed copies in the wild, and the operator's own
  three-installation migration through the new UI takes under a
  minute. Manual migration also preserves the install-level
  `CustomerKey`/`ProviderKey` fields for backwards-compat;
  clearing them via Edit Installation is left to the operator's
  discretion (until cleared, install-layer values continue to
  win the merge per precedence).

- **Plugin-defined Source-column formatting (Phase 5h-6).**
  New `ISourceLabelProvider` interface in `GSM.Contracts` lets
  plugins control how their rows render in the History window's
  Source column (see Changed). One method:
  `FormatSourceLabel(context As SourceLabelContext) As String`,
  invoked once per row at render time. `SourceLabelContext`
  carries `SessionIdentity`, `TileName`, `NodeName`,
  `InstallationName`, `InstanceName`, `InstanceId`, and
  `SharedConfigGroupName` (the user-set realm name from the
  linked SharedConfigGroup) — all pre-resolved by the manager
  so the plugin doesn't touch EF or the session-hosts table.

  **LO implementation:** three em-dash-separated segments —
  `{TileName} — {RealmDisplay} — {Node}/{Install}` — dropping
  any segment with no data. RealmDisplay prefers the linked
  group's DisplayName and falls back to
  `realm {first-8-of-realm_id}…` parsed out of SessionIdentity
  when no group is linked. Matches pre-5h-6
  `FormatSessionLabel` output for unlinked installs so visual
  experience for unlinked rows is unchanged — the upgrade is
  that linked installs now show the realm by name.

  **Manager dispatch:** new `HistoryQueryService.LoadResolvedInstances`
  pre-pass walks Instance + Installation + Node in one query
  and pulls SharedConfigGroup DisplayNames in a second, merging
  results into a per-InstanceId `ResolvedInstance` map. The
  new `ResolveSourceLabel` static helper builds the context,
  dispatches to the plugin via `PluginRegistry.GetPlugin(GameId)`,
  and falls back to `BuildDefaultSourceLabel`
  ("Node/Install/Instance", skipping empty segments) when the
  plugin doesn't opt in, returns Nothing, or throws. Plugin
  exceptions caught defensively — a misbehaving plugin's
  formatting bug shouldn't kill the whole query.

  `TimelineRow` and `SnapshotRow` both gained a `SourceLabel`
  property; `SnapshotRow` additionally gained `InstanceId`
  (captured from the join event during activity replay) since
  the existing snapshot pipeline didn't preserve it.

- **Show Logs toggle persistence per instance.** The
  InstancePanel's "Show Logs" toggle now persists across
  panel disposal and reconstruction, so navigating away
  from an instance and back keeps logs visible if they were
  visible before. Implementation is a class-shared
  `ConcurrentDictionary(Of String, Boolean)` keyed by
  InstanceId; the toggle writes its state on every user
  change, and a new `OnLoad` override reads the saved value
  and applies it (with a `_restoringShowLogs` flag to
  suppress the redundant write-back and the auto-select-
  Logs-tab side effect during restore). Restore runs from
  `OnLoad` rather than the constructor because
  `ShowLogsTab` uses `Me.BeginInvoke` for its deferred
  initial fill, which throws `InvalidOperationException`
  against a not-yet-parented UserControl. Manager-restart
  scope by design — a fresh manager session starts with
  logs hidden everywhere.

- **Last-selected tab persistence per panel type.** Both
  InstancePanel and InstallationPanel now remember the
  user's tab selection across navigation. Two separate
  class-shared `Private Shared` String fields (one per
  panel class) hold the last-selected tab's `.Text`; each
  panel's `OnLoad` walks `_tabs.TabPages` looking for a tab
  whose Text matches the saved value and selects it,
  guarded by a `_restoringTabSelection` flag to suppress
  the `SelectedIndexChanged` handler's write-back during
  restore. Text-keyed identity rather than index because
  dynamic tabs (Logs toggle on InstancePanel, plugin-
  supplied managed-files and editor tabs, Progress tab on
  InstallationPanel during install/update) shift indices
  across panels — Configuration might be at index 1 on a
  Last Oasis instance and 1 on a Factorio instance too,
  but with a different count of trailing tabs, so any
  index-based scheme would be brittle. Tabs that exist on
  only some panels (e.g., "Server Settings" on Factorio
  but not Last Oasis) fall through cleanly to the default
  tab when the saved name doesn't match. Handler hookup
  happens AFTER the initial tab Add calls in
  `InitializeControls` so the synthetic `SelectedIndexChanged`
  that fires on the first Add (SelectedIndex `-1 → 0`)
  doesn't pre-write a default tab name. Instance and
  installation preferences are deliberately independent —
  flipping through instances on Configuration doesn't drag
  installation panels along. May 2026 user feedback
  measured this as removing about 80–90% of the
  navigation clicks involved in comparing configurations
  or logs across instances during live operation.

- **Process re-adoption on node startup.** The node now reads
  its persisted `InstanceSnapshots` table at startup and
  re-attaches to game-server processes that survived the
  previous node session. For each snapshot row,
  `ProcessManager.AdoptSnapshots`:

  1. Calls `Process.GetProcessById(snapshot.Pid)` to find the
     live process (cleanly removes the snapshot if the PID is
     gone).
  2. Verifies identity by comparing `proc.StartTime.ToUniversalTime()`
     against the saved `StartedAtUtc`. Match tolerance is 60
     seconds, generous enough to cover system-clock
     adjustments during downtime (NTP correction at boot,
     manual time changes) and well within the timescale that
     would distinguish real PID reuse — Windows recycles PIDs
     over minutes-to-hours in practice, not seconds. To make
     the comparison effectively exact, `FinalizeStart` now
     records `proc.StartTime.ToUniversalTime()` rather than
     `DateTime.UtcNow` so writer and reader pull from the same
     kernel-fixed source.
  3. On match, rebuilds the `ManagedInstance` with the live
     `Process` handle, restores crash-policy fields from
     `CrashPolicyJson`, restores spawn metadata (Strategy,
     StdoutIsLog, RequiresConsoleIsolation,
     LogTailerStartDelayMs), reconstructs a `ProcessStartInfo`
     for post-adopt crash-restart, deserializes log file paths
     + parse rules from their respective JSON columns, attaches
     the same `OutputDataReceived` / `ErrorDataReceived` /
     `Exited` handlers as a fresh spawn, sets
     `EnableRaisingEvents=True`, registers parse rules with
     `EventStore`, starts file tailers (which resume from saved
     `TailerPositions` cursors), re-arms the crash-count-reset
     timer, and pushes the record into `_instances`.

  After the pass, the new node process is functionally
  indistinguishable from the prior one with respect to those
  instances: same crash detection (`Process.Exited` routes
  through `HandleProcessExited` normally), same graceful-stop
  path (`AttachConsole(pid)` in `GSM.CtrlCSender` works against
  any PID with a console regardless of which node process
  owns the handle), same manager-facing status reports. The
  manager's existing rule re-push on reconnect
  (`UpdateParseRulesAsync` from `EnsureLogStreamAsync`) layers
  on top to reconcile any plugin rule changes that happened
  while the node was down — the snapshot's rules are stale
  until that push but the window is typically the next 3-second
  poll cycle. Synchronous before `app.Run()` so endpoint
  requests never see a transient "everything is Stopped" view.

  Schema migration is additive: nine new columns on
  `InstanceSnapshots` (`ExePath`, `Arguments`,
  `WorkingDirectory`, `LogFilePathsJson`, `ParseRulesJson`,
  `Strategy`, `StdoutIsLog`, `RequiresConsoleIsolation`,
  `LogTailerStartDelayMs`) discovered via `PRAGMA table_info`
  so an upgrade-in-place picks them up once and a fresh
  install converges to the same final shape. Pre-migration
  snapshots have NULL in the recovery columns and are
  treated as un-adoptable (logged + cleaned up rather than
  crashing the load).

  Known limitation: Strategy A (`StdoutIsLog=True`, redirected
  stdio) game processes lose their stdout capture on adoption
  because the stdout pipe was owned by the previous node and
  is no longer connected. Neither LO (Strategy B) nor Factorio
  (Strategy C) hits this path today; theoretical for any
  future plugin that opts into A. Custom environment variables
  set at spawn time are not yet round-tripped through the
  snapshot — a post-adopt crash-restart for an instance that
  needed env vars would spawn without them. No current plugins
  use env vars, but follow-up if one does.

  Closes the node-binary-update workflow: stop node → swap
  binary → start node → instances re-adopted automatically,
  manager reconciles rules on next poll. Players stay
  connected; no operator intervention beyond the binary
  swap itself.

- **Manager re-pushes parse rules on reconnect to a running
  instance.** New `POST /api/instances/{id}/parse-rules` node
  endpoint accepts a `List<LogParseRule>` body and routes to a
  new `EventStore.UpdateParseRules` method that swaps the
  compiled rule list atomically under the state lock while
  preserving the per-instance Players, ServerState,
  PendingRemoteAddress, and PendingIdentitiesByPlatformUserId
  caches. The Manager's `EnsureLogStreamAsync` now invokes the
  matching `UpdateParseRulesAsync` client method right before
  resubscribing to the SSE log stream.

  **Scope clarification (May 12, 2026):** this refresh path
  only fires when the Manager restarts against a node that is
  STILL UP from the prior session. In that case the node's
  `ProcessManager._instances` still has the running instances
  registered with state=Running, so `EnsureLogStreamAsync`'s
  Running/Starting branch is taken and the rule push fires.

  It does NOT close the node-binary-update pain end-to-end
  by itself: on a node restart the new node process starts
  with an empty `_instances` dict (nothing reads the
  persisted `InstanceSnapshots` table on startup yet), so
  `GetInstanceStatus` reports `State=Stopped` for every
  running game process and the Manager skips the rule push.
  Closing that gap requires the process re-adoption work
  tracked in Backlog. The persisted `TailerPositions` table
  already covers log-event continuity for the file-tailed
  games (LO, Factorio) across a node restart — events
  written during the node-down window get streamed in by the
  tailer resuming from the saved byte offset.

  Graceful on older nodes — a 404 from the missing endpoint
  surfaces as `NodeApiException(StatusCode=NotFound)` and the
  reconnect proceeds without the refresh.

  **Now composes with process re-adoption (see above).** On a
  node restart, the new node first adopts the live game
  processes (rebuilding the `_instances` dict before
  `app.Run()`), so the manager's poll sees `State=Running`,
  triggers the rule re-push, and EventStore swaps to the
  current plugin rules while keeping the player/server-state
  caches the adoption rebuilt. The full node-update sequence
  is now: stop node → swap binary → start node →
  everything reconciles automatically. The earlier scope
  clarification ("only fires on manager-restart, not
  node-restart") no longer applies as of the adoption work.

- **History viewer "Instance" column.** The History timeline
  ListView now shows a fourth column titled "Instance" with
  the format `<NodeName>:<InstanceName>:<InstanceId>` per row,
  resolved via a single JOIN against Instances + Installations
  + Nodes once per query. The full InstanceId GUID is
  preserved (not truncated) because LO writes per-instance
  log files as `{InstanceId}.log` — keeping the raw string
  visible lets an operator grep the on-disk log for the exact
  line that produced any chat / join / leave row. Rows whose
  InstanceId no longer resolves to a live (instance,
  installation, node) triple render as
  `(deleted):(deleted):{instanceId}` so retrospective
  debugging of removed servers still works. Snapshot mode
  is unaffected for now — the column is timeline-only since
  that's the event-anchored view where the lookup matters.

- **Three-key player identity resolution in `EventStore`.**
  Player records now merge partial events via any known
  identity key — `CharacterId` (primary, from the Login
  line), `PlatformUserId` (from `Processing character
  update`), `DisplayName` (from `Persisting`), with
  secondary fallbacks on `RemoteAddress` and
  `PlatformPersona`. The `Persisting <DisplayName>,
  UniqueNetId = <Platform>:<PlatformUserId>` log line is now
  a recognised `PlayerIdentity` event in the Last Oasis
  plugin, bridging DisplayName ↔ PlatformUserId. Combined
  with the existing `Processing character update` line
  (PlatformUserId ↔ CharacterId) and the Login line
  (PlatformPersona + CharacterId), the three log lines form
  a complete identity chain over PlatformUserId without
  external API dependency or session-cookie scraping.

- **Pending-identity stash for race-window handling.** When
  a Persisting line fires for a player whose
  Processing-character-update hasn't landed yet (the typical
  case for a player whose connection arrived mid-autosave
  tick), the (DisplayName, PlatformUserId, Platform) tuple
  is stashed in
  `InstanceEventState.PendingIdentitiesByPlatformUserId`
  keyed by PlatformUserId. The next event that resolves
  PlatformUserId to a session drains the stash, applying
  the deferred DisplayName binding. Stash entries are
  removed on session leave so they don't accumulate across
  long-running sessions; process restart resets in-memory
  state entirely.

- **Chat-as-DisplayName-source fallback.** Post-5g-1 Last
  Oasis builds emit `Persisting <DisplayName>, UniqueNetId
  = ...` only at player departure (~250ms before
  disconnect, which the manager's 3-second poll cycle
  reliably misses). Without a second source, renamed
  characters would show their Steam persona for the entire
  session and only flip — if at all — right as they're
  leaving. The chat handler now writes the speaker back to
  the session's `DisplayName` when a name-based lookup
  matches an existing session OR when exactly one player is
  tracked on the tile (single-player fallback). Multi-player
  tiles with simultaneous unresolved renamed players fall
  through with no attribution rather than guessing — chat
  rows persist with the speaker as `DisplayName` text but no
  `PlatformUserId`/`CharacterId` linkage, and the live
  player list keeps showing PlatformPersona for those
  players until 5g-2 ships the persistent DisplayName
  lookup at Login.

- **`-EnableCheats` as a default Last Oasis launch
  argument.** Admin chat commands (kick, ban, give,
  teleport, etc.) require this flag at server launch —
  without it the command parser is disabled and admin chat
  lines are silently ignored. Was previously the operator's
  responsibility to add via custom args; now on by default
  since most operators want it and forgetting it produces
  a confusing "my commands don't do anything" symptom with
  no error feedback.

- **Render-time chat fallback in the History window.**
  `HistoryQueryService.LoadTimeline` now backstops the
  write-time identity snapshot for activity rows whose
  `DisplayName` came back empty or equal to the raw
  `PlayerName`. For these rows, a render-time lookup
  against `ChatMessages` by `(SessionIdentity,
  PlatformUserId)` pulls the most recent chat
  `DisplayName` the player used and overrides
  `TimelineRow.PlayerName`. One indexed query per
  distinct (sid, pid) pair, leveraging the `IX_chat_pid`
  index from 5g-1. Handles edge cases the write-time
  snapshot can't cover: returning players joining on a
  Node whose `players` table doesn't have them yet
  (cross-Node migration, fresh `players.db`), the
  pre-5g-2b Conan case where the snapshot caught the
  FLS handle in both slots, and the short-session race
  where the Node hadn't yet resolved DisplayName at
  snapshot time. Players who never chatted within the
  queried scope fall through to the raw `PlayerName`.

### Fixed

- **Manager-side log-stream doubling on instance restart.**
  Stopping and restarting an instance produced every-line-
  doubled log output for the rest of the instance's session
  — not just startup bursts, but slow steady-state lines
  too. Root cause was a race between `StartInstanceAsync`'s
  success-path call to `StartLogStream` and
  `BackgroundPollLoopAsync`'s stream-health check: the
  background poll could observe
  `_liveStates(id).CurrentState = Running` and an empty
  `_logStreamCancellations(id)` during the brief window
  between the manager setting the state and the call chain
  reaching `StartLogStream`. Both callers then ran
  `StartLogStream` concurrently, and the dict assignment
  (`_logStreamCancellations(instanceId) = cts`) was a naked
  upsert — overwrote without cancelling the prior cts. The
  previous task's `CancellationTokenSource` was now orphaned
  (no longer in the dict, no remaining reference to call
  `Cancel()` on), and its background SSE consumer ran
  forever in parallel with the new one. Every line emitted
  by the instance arrived via both subscribers and got
  written twice to the manager ring buffer. Fix is an
  idempotent `StartLogStream`: under a new `_logStreamLock`
  SyncLock, the method `TryRemove`s any existing entry,
  calls `Cancel()` + `Dispose()` on it, clears the stale
  `_logParsers` entry, then installs the fresh cts.
  Whichever caller reaches the lock second cancels the
  first, and the orphaned task's existing compare-and-remove
  in its Finally sees a mismatched cts in the dict and bails
  correctly. `Task.Run` runs INSIDE the lock so parser
  registration in `_logParsers` happens before the streaming
  task starts — otherwise the new task could read lines
  while a previous parser is still registered.

- **Rich-text log viewer beep cascade.** With the Logs tab
  open during a Last Oasis startup burst, the Windows system
  ding sound played continuously for as long as lines were
  flowing in. Rich-edit responds to `EM_REPLACESEL` on a
  `ReadOnly = True` control by calling `MessageBeep` BEFORE
  performing the replacement — the append still succeeds
  (which is why the log content was visible correctly), but
  every call rings the system bell. `RichTextBox.AppendText`,
  `SelectedText = ""`, and the trim path's `Select() +
  SelectedText = ""` all funnel through `EM_REPLACESEL`, so
  any one of them is enough to produce the cascade. Fix in
  `InstancePanel.AppendLogLinesToTab` brackets the redraw-
  suspended mutation block with `_logTextBox.ReadOnly =
  False` at the start and restores `= True` in the Finally
  alongside the WM_SETREDRAW re-enable. The toggle window is
  invisible to the user because the `WM_SETREDRAW = 0`
  across the same span prevents the rich-edit from
  processing input events while the flag is flipped.

- **UE4 dedicated-server log tailing on Linux nodes.** The
  node's file tailer used `New FileStream(path, FileMode.Open,
  FileAccess.Read, FileShare.ReadWrite Or
  FileShare.Delete)`, which works on Windows but fails on
  Linux when the UE4 process has the file open with an
  advisory `flock(LOCK_EX)`. .NET 8's `FileStream` on Linux
  consults the advisory lock and refuses the open; `lsof`
  showed `MistServ ... 3uW` (fd 3, mode r+w, capital W =
  write lock on the entire file). Fix is a libc.open
  bypass: `<DllImport("libc")> LibcOpen(path, O_RDONLY)`
  returns a raw fd that ignores the advisory flock entirely,
  wrapped in a `SafeFileHandle(handle, ownsHandle:=True)`
  and passed to `New FileStream(handle, FileAccess.Read)`.
  Windows continues to use the regular FileStream
  constructor (no flock semantics there).
  `OpenLogFileForTailing(path)` encapsulates the platform
  switch so callers don't have to know.

- **Spawn-path file-tailer duplication regression.** A fresh
  instance start on Strategy A (StdoutCapture, the path
  Linux LO is forced into via `ResolveStrategy`) was starting
  BOTH the stdout-capture ingest AND a file tailer for the
  same .log file, producing exactly one duplicate per line.
  The adoption path needs the file tailer because it has no
  stdout pipe to inherit, but the fresh-spawn path doesn't.
  `ProcessManager.FinalizeStart` now gates the tailer start
  on `If managed.Strategy <> SpawnStrategy.StdoutCapture
  Then StartFileTailers(...)`. The adoption path in
  `TryAdoptOne` unconditionally starts file tailers as
  before. Strategy B (Windows hidden console) and Strategy C
  (Linux Factorio with native terminal) are unaffected
  since they don't capture stdout for the log buffer either
  way.

- **Linux file-tailing gap for UE4 verbose categories.** The
  duplication fix above ("Spawn-path file-tailer duplication
  regression") over-corrected for Linux + file-logged games:
  with the gate set to "Strategy = StdoutCapture means no
  tailer" and Linux forced onto StdoutCapture by
  `ResolveStrategy` (CREATE_NEW_CONSOLE is Win32-only, so
  there's no Strategy B/C path on Linux), a Linux LO instance
  ended up with stdout as its only log source. The UE4 Linux
  console output device filters at Display verbosity by
  default — the documented "mirror everything to stdout and
  stderr" behaviour does not hold for Verbose-category lines.
  `LogPersistence: Verbose: Processing character update` and
  `LogPersistence: Verbose: Persisting <name>'s character`
  never reached the EventStore, so player sessions on Linux
  instances never bound `PlatformUserId`, Persisting lines
  couldn't correlate to the session via pid lookup, and
  in-game character names never resolved past the Steam
  persona. The Windows instance of the same plugin ran on
  Strategy B and tailed the file directly, which is why it
  worked there on identical EventStore + plugin code.

  Fix moves the duplication-avoidance condition off of
  `Strategy` and onto `CaptureStdout`, which becomes the
  single source of truth for whether stdout duplicates the
  file. `StartInstanceAsync` now sets `CaptureStdout = True`
  only when the plugin has NOT declared file log sources —
  if it has, the file is the authoritative source, stdout
  gets drained (so the child doesn't block on a full pipe
  after ~4KB) but its data is not forwarded to the ring
  buffer or EventStore. `FinalizeStart` then starts file
  tailers whenever `hasFileLogs AndAlso Not CaptureStdout`,
  which captures all three cases correctly: Linux
  Strategy A + file logs (new behaviour, file is the source
  via libc.open tailer), Windows Strategy B/C + file logs
  (unchanged, file is the source via FileStream tailer), and
  any Strategy A without file logs (unchanged, stdout is the
  source). `TryAdoptOne` applies the same hasFileLogs-aware
  CaptureStdout assignment so a post-adopt crash-restart
  spawns with behaviour matching the original. Adoption path
  still unconditionally starts tailers since the stdout pipe
  of an adopted process can't be re-attached regardless.

- **Ghost "Unknown" player entries for persisted-but-not-
  connected characters.** The Linux file-tailing fix above
  exposed a second-order bug: with the tailer now running on
  Linux, EventStore began seeing every `LogPersistence:
  Verbose: Processing character update` line UE4 emits —
  including the ones fired during server boot for every
  character persisted on the tile, and during autosave ticks
  for characters whose players are offline but whose bodies
  still occupy the tile. The Last Oasis plugin classified
  that line as `Kind = PlayerJoin` (to close a world-travel
  race documented inline in the plugin), so each of those
  loads called `FindOrCreateSession` and materialised a
  session in `state.Players` with `cid + pid` and no
  `PlatformPersona`/`DisplayName` — the Manager player list
  rendered each as "Unknown" because both name surfaces
  were empty. On a tile that retained, say, twelve persisted
  characters from prior sessions, the player list would
  immediately show twelve Unknowns on instance start, with
  no actual players online.

  Fix is a design change to the world-travel correlation:
  the LO plugin now classifies Processing-character-update
  as `Kind = PlayerIdentity` (enrichment-only), and the
  EventStore carries a new cid-keyed pending-identity stash
  alongside the existing pid-keyed one. When the event
  arrives before any session exists for the CharacterId, the
  `(PlatformUserId, Platform)` pair stashes under the cid in
  `InstanceEventState.PendingIdentitiesByCharacterId`. When a
  subsequent Login creates the session via
  `FindOrCreateSession`, a new `DrainPendingCidIdentity`
  helper applies the stashed pid to the session — closing
  the same world-travel race the PlayerJoin classification
  did, but without materialising a session for events that
  fire without an associated network connection. Stash
  entries that never get drained (no Login ever arrives for
  that cid) sit idle until the instance is unregistered;
  bounded by the persisted-character count on the tile,
  typically < 100.

  `DrainPendingCidIdentity` must run BEFORE
  `DrainPendingIdentity` in the enrichment flow — the former
  sets PlatformUserId on the session, which the latter then
  uses to look up the pid-keyed (DisplayName, Platform)
  stash. PlayerJoin and PlayerIdentity cases both call them
  in this order. PlayerLeave cleanup removes from both
  stashes so neither accumulates across long-running
  sessions. The `PendingIdentity` class gained a
  `PlatformUserId` field used only on cid-keyed entries
  (pid-keyed entries use the dict key itself).

  Sequence trace for the three relevant scenarios under the
  new design:

    World-travel arrival (Processing-character-update fires
    before Login): cid stash captures (pid, Platform); Login
    creates the session; DrainPendingCidIdentity applies pid;
    Persisting (if pid stash had landed) drains via
    DrainPendingIdentity.

    Fresh connect (Login fires before Processing-character-
    update): Login creates the session with cid+persona;
    Processing-character-update finds the existing session
    by cid and enriches with pid directly via the
    PlayerIdentity enrichment branch — no stash involvement.

    Persistence-only events (server boot, autosave of
    offline-on-tile characters): cid stash accumulates entries
    for characters with no current connection; player list
    stays correctly empty. Stash drains naturally if those
    characters ever Login, or is dropped on instance
    unregistration.

- **LO Persisting regex truncated names ending in `'s
  character`.** The Persisting-line capture in the Last Oasis
  plugin assumed UE4 appends a literal `'s character` suffix
  to character names in `LogPersistence` output — the regex
  was `Persisting (?<DisplayName>.+?)'s character, UniqueNetId
  = (?<Platform>\w+):(?<PlatformUserId>\d+)`. That assumption
  is wrong: UE4 emits character lines as `Persisting <Name>,
  UniqueNetId = <Platform>:<UID>` with no appended suffix.
  Any in-game name happening to end in `'s character`
  (which is a natural way to name a character; the on-card
  display in-game shows it directly) got silently chopped at
  the regex's expected suffix — `site's character` captured
  as just `site`, persisted into the `players` table under
  that truncated form, and the cached name was then used to
  hydrate the player list on every subsequent join. The user
  saw their character's correct name appear briefly only on
  the rare occasions a Persisting tick landed during an
  active session, immediately replaced by the truncated
  cache on the next reconnect.

  Fix changes the anchor from `'s character,` to `,
  UniqueNetId` — the literal token that discriminates the
  character-shaped line from the actor-shaped one
  (`Persisting <ActorClass>, ActorGuid = {GUID}`, which uses
  `, ActorGuid =` instead and never matches). The non-greedy
  capture still backtracks through any commas embedded in the
  name until the `, UniqueNetId` anchor matches, so names
  like "andre, the wanderer" capture fully. Stale truncated
  entries already in the `players` table get overwritten on
  the next Persisting tick after a player joins, so no
  one-shot cleanup is needed — the natural autosave cadence
  fixes the cache within ~2 minutes of the affected player's
  next session.

- **Chat duplication on adoption replay.** The
  `skipResume:=True` parameter introduced for node-adoption
  EventStore rebuilding prevented the in-memory caches from
  re-firing notification events on replayed lines, but the
  chat persistence path still called `INSERT INTO
  chat_messages` with `timestampUtc = DateTime.UtcNow` taken
  from `EmitTailLine`. On every adoption, the entire ring
  buffer's chat lines re-flowed through `ProcessLine` and
  got persisted again with fresh server-side timestamps,
  producing duplicate rows that diverged only in timestamp.
  Fix is two-pronged: a new `TryParseUe4Timestamp(text)`
  extracts the `[YYYY.MM.DD-HH.MM.SS:fff]` prefix on UE4
  lines and uses that as the persisted `timestamp_utc`, and
  a new `ux_chat_dedup` UNIQUE INDEX on `(instance_id,
  timestamp_utc, display_name, text)` plus a switch to
  `INSERT OR IGNORE` makes the persistence idempotent
  regardless of replay count. Lines without a parseable UE4
  timestamp (Factorio, plain text) fall back to
  `DateTime.UtcNow` and are still de-duped by the index —
  the practical collision rate on real chat is negligible.

- **Node SSE backfill / live-stream subscription race.**
  The Last Oasis startup burst (hundreds of lines in 2-3
  seconds) was producing doubled lines on FRESH stream
  subscriptions — distinct from the manager-side double-
  subscription bug above, and visible on a single subscriber
  alone. Root cause in `InstanceBuffer.StreamToResponseAsync`:
  the old code took the buffer's internal SyncLock twice in
  sequence — once via `AddSubscription` which set
  `subscription.LastSequence = _writePos - 1`, and once via
  `GetTail(tailLines)` which read `_writePos` again. Between
  the two acquisitions, an `Append` could fire and bump
  `_writePos`; the new line then appeared in BOTH the tail
  returned to the client AND in
  `GetLinesSince(LastSequence)` on the subscription's first
  live-stream read. Fix is a new
  `SubscribeAndGetTail(subscription, tailCount)` method that
  takes a single SyncLock and uses one consistent
  `_writePos` snapshot for both halves: tail returns
  `(_writePos - take)..(_writePos - 1)` and live stream
  starts at `_writePos`, no overlap and no gap. Legacy two-
  call path remains as a deprecated entry point for callers
  that don't need both halves.

- **Manager-side `SessionIdentity` fallback for adopted
  instances.** The Last Oasis parser's session identity is
  committed by a 4-line tile-load sequence (`Started hosting
  tile` → realm_id → tile_name → tile_id), but on adoption
  that sequence can be hours old and has rotated out of the
  node SSE ring buffer (4096 lines). The manager parser came
  up with `CurrentSessionIdentity = Nothing`, and any chat
  or player-activity rows recorded on the parser's first
  hour after adoption went to disk with empty session
  identity, orphaning the rows from any tile context. Fix
  is a layered `ResolveSessionIdentity` helper: parser-
  committed identity first (live path, unchanged), then a
  per-instance in-memory cache, then a SQLite lookup against
  `SessionHosts WHERE InstanceId = ? AND HostedUntilUtc IS
  NULL ORDER BY HostedFromUtc DESC LIMIT 1` to find the
  most recent open hosting record, then finally a
  synthesized `{gameId}:{instanceId}` if nothing matches.
  Self-healing: once the parser commits a real identity
  (e.g., when the next 4-line sequence fires on tile
  change), the cache invalidates and future lookups bypass
  the DB. Cache is dropped on instance stop via
  `ClearPlayerTracking`.

- **Linux Ctrl+C signal isolation for game children.**
  Stopping the node service via Ctrl+C on Linux was also
  killing every game-server child because the kernel routes
  SIGINT to the controlling terminal's entire process
  group. Game children spawned by the node were in the same
  process group by default. Fix wraps the spawn in `setsid`
  on Linux: `ProcessManager.WrapInSetsidIfLinux(psi)`
  rewrites the `ProcessStartInfo` so the child runs as
  `setsid <exe> <args>`, detaching it into a new session
  and process group. The node's own Ctrl+C handler still
  signals its game children explicitly via the gsm-broker
  path when an instance stop is requested; the only thing
  setsid blocks is incidental terminal propagation.
  Idempotent — calling on a psi that's already setsid-
  wrapped is a no-op.

- **Last Oasis Linux server authentication and AppID file.**
  The Linux Last Oasis server (`MistServer-Linux-Shipping`)
  was failing its Steam authentication on launch because
  the Linux distribution requires (1) `Mist` as positional
  argument 0 in the launch command, and (2) a
  `steam_appid.txt` file in `Mist/Binaries/Linux/` (the OS-
  specific binaries directory) rather than the install
  root. `LastOasisPlugin.BuildLaunchArguments` now prepends
  `"Mist"` to the argv list on Linux only, and the
  `WriteFileStep` for `steam_appid.txt` resolves to the
  platform-specific path. Windows is unaffected.

- **`DateTime.Parse` adoption crash on node startup.**
  `NodeProgram.LoadAllInstanceSnapshots` was passing both
  `DateTimeStyles.RoundtripKind` and
  `DateTimeStyles.AssumeUniversal` to `DateTime.Parse`. The
  two are mutually exclusive — RoundtripKind says "honor
  the kind designator in the string", AssumeUniversal says
  "force UTC on un-designated strings". .NET 8 throws
  `ArgumentException` rather than silently picking one,
  which crashed the node startup adoption pass entirely.
  Fix is to drop `AssumeUniversal` — every snapshot
  timestamp this code reads is written by
  `ToUniversalTime().ToString("o")` which always includes
  the `Z` designator, so `RoundtripKind` alone produces
  correct UTC parsing.

- **Steam Guard email spam from periodic version checks.** The
  Manager's `CheckForUpdatesAsync` was unconditionally
  resolving the installation's stored Steam credentials and
  sending them to the node for the version check. On nodes
  Steam didn't recognise — typically Linux nodes on
  residential connections that don't share a fingerprint with
  the user's normal Steam client — every hourly check
  triggered a Steam Guard challenge against the account,
  blasting the user's inbox with verification-code emails
  they never asked for and reporting "failed" since the
  challenge couldn't be answered in the polling path. The
  request now sends `SteamCredentials = Nothing`, which the
  node-side `CheckAppVersionAsync` was already wired to
  interpret as `+login anonymous`. `+app_info_print` is a
  read-only public-metadata query against Steam's app DB —
  the public branch's `buildid` is exposed without a license
  check, which is why anonymous works for paid apps too
  (what requires authentication is `+app_update`, the depot
  download, which is the install/update path and unaffected
  by this change). On a node that's been getting Steam Guard
  spam, the next version-check cycle should run quietly with
  no email and produce a usable buildid for the
  `LatestKnownVersion` comparison.

- **"Check for Updates" surfaces actual error messages.**
  `VersionCheckService.CheckInstallationAsync` previously
  returned `Task(Of Boolean)`, swallowing every failure mode
  into a `False` return and leaving the manual-button UI to
  fall back to a `"Check failed (see log for details)"`
  placeholder. Return type is now `Task(Of VersionCheckResult)`
  with a populated `ErrorMessage` on every failure path —
  installation not found, plugin not loaded, Steam-side
  error message from the node, plugin exception, empty
  upstream response, outer Catch. The InstallationPanel's
  manual `OnCheckForUpdates` handler renders short errors
  in the existing status label below the button, and routes
  long or multi-line errors (the multi-line SteamCMD
  missing-libs hint from the Linux pre-flight being the
  motivating case at ~500 chars across multiple lines) into
  a resizable monospace dialog via the new shared
  `DetailedErrorDialog` helper. The label still shows a short
  first-line summary in the dialog case so the post-dismiss
  state is informative. Threshold is `> 150 chars OR contains
  a newline`, picked to match "doesn't fit in a 400px-wide
  AutoSize label" in practice.

- **Version label clarified to "last successfully checked".**
  The InstallationPanel's version line previously rendered
  the freshness suffix as `, checked Xh ago`, which read
  ambiguously when the timestamp belonged to a long-ago
  success and recent checks had been failing. `LastVersionCheckUtc`
  is, by design, only updated on a successful check (see the
  header comment on `VersionCheckService`) — a transient or
  permanent failure leaves the timestamp untouched so the
  poller retries promptly. The label now reads `, last
  successfully checked Xh ago`, making the success-only
  semantics visible at a glance: a user looking at
  `(update available, last successfully checked 14h ago)`
  knows the timestamp isn't lying about recent failures, just
  about the last good result. No schema or data change —
  string-only.

- **In-tab player list now drops players on UE4 control-channel
  close.** The node-side Last Oasis PlayerLeave parse rule
  matched only `LogNet: UNetConnection::Close:` lines, but some
  Last Oasis disconnect flows fire only `LogNet: UChannel::Close:
  ... ChIndex == 0 ... RemoteAddr: <addr>,` and never produce a
  separate `UNetConnection::Close` line at all — the channel-0
  (control-channel) close IS the disconnect signal. EventStore
  never removed the player from its in-memory `state.Players`
  dict, so the in-tab player list kept showing them indefinitely.
  The History viewer captured the leave correctly via the
  manager-side parser, which already matched both close forms,
  producing a visible asymmetry between live status and history.

  The rule now matches `UChannel::Close:` with `ChIndex == 0`
  OR `UNetConnection::Close` and captures the RemoteAddr from
  either form, which is enough for the EventStore's
  RemoteAddress-based session lookup to resolve and remove the
  session. The `ChIndex == 0` guard restricts the new branch to
  the control channel — actor channels close mid-game without
  meaning a player disconnect, and matching them would produce
  false-fire removals. Per-event idempotency (FindExistingSession
  returns Nothing once the session is gone) makes a redundant
  later UNetConnection::Close on the same disconnect a safe no-op.

- **Conan parse rules mis-captured the FLS handle as
  `DisplayName`.** The Conan `Join succeeded:` and
  `Player disconnected:` log lines carry the FLS handle
  (Funcom-issued account identifier, e.g.
  `losno420#72569` or bare `blingity`) as their
  post-colon token, NOT the in-game character name —
  character names land later via chat lines or the
  Node's persistent players-table cache. The plugin's
  parse rules were capturing this token into the
  `DisplayName` group, polluting the Node's
  `PlayerSession.DisplayName` slot with platform-identity
  data and producing FLS-handle entries in the History
  window's Join/Leave rows for a character whose Chat
  rows correctly rendered the character name. Both
  captures renamed to `PlatformPersona` so the slot
  semantics match Last Oasis: FLS handle goes to the
  platform-identity slot (stable for the session's
  lifetime), DisplayName is free for the character name
  to land via chat or cache. Also closes a latent
  leave-side bug where, after chat had flipped the
  session's DisplayName to the character name, a leave
  event capturing the FLS handle into DisplayName would
  no longer match the session via the DisplayName key
  (fell through to RemoteAddress match, which works but
  is fragile under simultaneous disconnects). Conan
  `PlayerActivity` rows written before this fix are not
  backfilled and continue to render the FLS handle in
  the History window; sessions starting after the fix
  render the character name once chat has fired in the
  session or the Node's cache has the binding from a
  prior session. Currently-running Conan instances need
  affected players to disconnect and reconnect once for
  the new binding to take effect on their session — the
  Manager pushes the new parse rules to the Node
  automatically via `UpdateParseRulesAsync`, but the
  Node's in-memory `PlayerSession` state for already-
  connected players isn't re-evaluated.

- **Conan `Map Loaded` rule was a silent no-op.** The Conan
  plugin's parse rule for `LogWorld: Bringing World <MapPath>
  up for play` had `Kind = ParsedEventKind.Custom` while
  capturing into a well-known group name (`MapPath`, not
  `Custom_MapPath`). The Custom kind has no `Select Case`
  branch in `EventStore.ApplyMatch`, so the captured value
  didn't reach `ServerState.CurrentMapPath`; and
  `HarvestCustomFields` only scrapes capture groups whose
  names start with `Custom_`, so it didn't pick up the value
  either. The rule fired on every Conan boot and did
  literally nothing. Fix is a one-token change to
  `Kind = ParsedEventKind.TileLoaded`, matching the LO
  plugin's identical "Bringing World" rule.
  `CurrentMapPath` now populates on Conan instances and
  mirrors to `instance_state` for node-restart survival via
  the existing `PersistInstanceStateSnapshot` call.
  `TileId` / `TileName` stay empty since Conan doesn't use
  LO's tile model. No Manager-side rendering of
  `CurrentMapPath` exists yet on either game, so this is
  preparatory for future Overview UI rather than an
  immediate visible change — but it closes the
  inconsistency between the two identical-shape parse rules
  and gives Conan parity with LO on the contract-defined
  field.

### Notes

- **Residual identity-resolution gaps.** Two narrow cases
  remain where `PlayerActivity.DisplayName` can't be
  resolved at write time AND the render-time chat
  fallback can't bridge through (player never chatted in
  the queried scope), so the History window falls back to
  the raw `PlayerName` — Steam handle on Last Oasis, FLS
  handle on Conan:

  - **Short LO sessions.** A player joins, leaves before
    chatting AND before the first `Persisting` autosave
    tick lands (~2-minute window on post-May-2026 LO
    builds where Persisting fires only at departure),
    AND no prior session on the Node has cached their
    PlatformUserId→DisplayName mapping. Tracked as Phase
    5g-3 in the backlog; the hypothesis is that a richer
    transitive identity graph using LO's `Player_0_C` /
    `OasisPlayerController_0_C` / `ActorGuid` actor
    surfaces could close it.

  - **First-time-ever Conan players who don't chat.** The
    Conan `Character ID <n> has name <Name>` spawn line
    carries CharacterId + character name but no
    PlatformUserId, which the Node's current
    `PlayerIdentity` stash machinery can't bind to a
    session (it has paths for `pid+display, no cid` and
    `cid+pid, no display` but not for `cid+display, no
    pid`). Tracked as Phase 5g-2c in the backlog; closing
    it needs a third EventStore stash path plus a
    heuristic to drain it on subsequent chat.

  Both cases self-heal on a future session where the
  player either chats or has been chatted-before, since
  the Node's persistent `players` table caches the
  binding on first resolution. Pre-5g-2 Last Oasis rows
  and pre-5g-2b Conan rows also remain rendered under
  their original raw-PlayerName values; both phases
  intentionally skipped backfill (false-positive risk
  outweighs edge-case recovery) and there's no plan to
  revisit.

- **Conan InstancePanel "Steam name" column is
  technically misleading.** The column shows whatever
  lives in `PlayerSession.PlatformPersona`, which on
  Last Oasis is the actual Steam handle (or Xbox
  gamertag) but on Conan post-5g-2b is the FLS handle.
  Funcom doesn't log the Steam display name; the FLS
  handle is the most useful platform-identity surface
  available. Tracked as a cosmetic backlog item;
  candidate fixes are a generic "Persona" relabel or a
  plugin-driven column label.

## [0.2.0] - 2026-05-08

### Fixed

- Factorio direct-download installs on Linux now extract via
  native `tar` instead of SharpCompress 0.36.0. The previous
  extractor's Pax-extended-header handling didn't recognise
  the BSD-tar variant Factorio's build pipeline emits, so any
  entry with a path longer than the 100-char standard tar
  header limit landed on disk with its name truncated at
  boundary 100 — `rail-chain-signal-elevated.lua` became
  `rail-chain-signal-elevated.l`, the elevated-rails mod's
  `require()` chain failed at engine init, and map generation
  died with exit code 1 before any useful diagnostic surfaced.
  Native `tar -xJf` reads Pax records correctly, applies the
  long names, and `--strip-components=1` collapses the
  archive's `factorio/` wrapper directory in one flag — replaces
  the manual staging-and-hoist dance the SharpCompress branch
  needs to do by hand. SharpCompress remains the fallback for
  any future Windows direct-download case.
- Factorio direct-download installs on Linux now preserve the
  executable bit on `bin/x64/factorio`. SharpCompress's
  `WriteEntryToDirectory` doesn't apply the tar entry's unix
  mode field — files extracted with the process default umask
  (typically 0664), which left the Factorio binary unable to
  launch via `Process.Start` (errno 13 EACCES). Native tar
  applies modes during extraction; the SharpCompress fallback
  path also now calls `File.SetUnixFileMode` per entry on
  Linux as a backstop.
- Factorio direct-download installs no longer perpetually
  report "update available" immediately after a fresh install.
  The manager-side `BuildVersionStamp` produced
  "installed (timestamp)" strings that could never match the
  canonical "2.0.76" version the factorio.com API returns,
  so the version-check loop reported drift on every poll.
  Plugins implementing the new
  `IVersionAwarePlugin.GetInstalledVersionAsync` hook now
  stamp the installed-version field with a value that
  compares cleanly against `GetLatestVersionAsync`. Factorio
  reads `data/base/info.json` for it; the
  `VersionCheckService` also opportunistically re-reads on
  every poll cycle to upgrade pre-existing rows without
  requiring a reinstall.
- Factorio direct-download updates are no longer silent
  no-ops. `GetUpdateSteps` lacked a `DirectDownload` branch
  entirely, so updates on direct-download installs returned
  an empty step list; the runner executed zero steps and
  recorded "completed successfully" with no
  Download/Extract/Configure entries between the bookends.
  The plugin now emits a parallel branch matching the install
  path (re-fetch tarball, extract over existing files,
  re-write `config-path.cfg`).
- Factorio direct-download tarballs no longer leave the
  install layout one level too deep. The headless tarball
  wraps every entry under a `factorio/` top-level directory,
  which left plugin-relative paths like `bin/x64/factorio`
  and `data/base/info.json` resolving against
  `<install>/factorio/...` instead of `<install>/...`.
  Plugins now request top-level stripping via the new
  `DownloadFileStep.StripTopLevelDirectory` flag — native
  tar implements it via `--strip-components=1`, the
  SharpCompress fallback via a staging-and-hoist pass.
- Factorio direct-download installs no longer leak
  `@PaxHeader` pseudo-files into the install root. The
  BSD-tar pipeline emits Pax extended headers as type-flag-
  incorrect entries that SharpCompress treats as regular
  files. The native-tar branch consumes them as metadata;
  the SharpCompress fallback filters entries whose path
  segments match the `PaxHeader` / `@PaxHeader` /
  `PaxHeaders*` patterns.
- The Generate Map failure dialog now surfaces the engine's
  captured stdout/stderr in a resizable, monospace TextBox
  scrolled to the end. Previously the captured output
  existed in the `GenerateMapResponse.Output` field but was
  dropped by the UI's status-label-only rendering whenever
  the bare error message ran over 80 characters — the user
  saw `Process exited with code 1 (expected 0): ...` with
  no diagnostic context. Reused for any future plugin-
  driven file-generation operation that fails with engine
  output.

- Chat messages, player joins, and player leaves on Factorio
  instances no longer get re-ingested on every instance restart.
  Previously the tailer re-read the log file from the beginning on
  each start, causing EventStore to re-emit all prior events and
  produce duplicate rows in the Chat tab (the same message would
  appear three times after three restarts, each with a different
  timestamp).
- The History timeline now records a Leave event for every player
  who was online when an instance stops or crashes, instead of
  leaving dangling joins with no matching leaves. Manager-side
  player tracking flushes synthetic leave rows to PlayerActivity
  on terminal-state transitions (Stopped/Crashed/CrashLoopHalted)
  in addition to the existing graceful-stop path.
- Chat messages persisted to the node after a Manager restart no
  longer get silently filtered out of the manager's mirror. The
  cursor seeded from EF Core's SQLite store came back with
  `DateTimeKind=Unspecified`, which serialised without a `Z`
  suffix; the node parsed that as a local-time value and shifted
  the cursor forward by the manager's UTC offset, causing every
  chat between the original cursor and (cursor + offset) to be
  excluded from the response. `SeedChatCursor` now tags the value
  as UTC, and the node endpoint treats `Unspecified`
  `since` parameters as UTC defensively. Chats missed during the
  bug window will be back-mirrored on the next manager start.

- Per-node connection-failure log dedup. Multi-instance nodes
  going offline used to produce one warning per instance per
  3-second poll cycle — a 4-instance node down generated ~80
  warnings every 5 minutes, drowning out everything else.
  Now deduplicated per-node: the first failure logs once with
  `(further failures will be suppressed for up to 5 minute(s))`,
  a heartbeat every 5 minutes if the node's still unreachable
  so an operator who arrives mid-outage still sees the state,
  and the recovery line names the downtime
  (`Node X reachable again (was unreachable for 12m;
  suppressed 47 duplicate warning(s))`).
- Steam-managed installations now stamp `InstalledVersion`
  with a real buildid. Previously SteamCmd installs stamped
  a synthetic `installed (timestamp)` string that could never
  match the canonical `steam:{appId}@{branch} build {N}`
  format the `VersionCheckService` produces from
  `app_info_print` output, so every poll cycle reported drift
  on every Steam-managed installation. The node now reads the
  buildid from `appmanifest_{appid}.acf` after a successful
  install and surfaces it via the new
  `InstallProgressResponse.InstalledBuildId` field; the
  manager stamps `InstalledVersion` directly in the comparable
  format and no longer needs the previous fire-and-forget
  post-install version-check round trip.
- ANSI escape sequences stripped from SteamCMD stdout and
  stderr. Linux SteamCMD wraps every line in CSI sequences
  when writing to a pipe (`\x1b[0m...` resets and colour
  codes); without stripping, log files contained visible
  `[0m` artefacts and the manager's message field showed
  gibberish. Stripping happens at stdout/stderr receipt and
  again defensively at the content_log parser entry, so log
  files, the message field, and regex matching all see clean
  text. Windows SteamCMD doesn't colour its output, so this
  is a no-op there.
- Linux SteamCMD installs now report progress during the
  Downloading phase. The I/O counter poller (which derives
  bytes from `wchar` in `/proc/<pid>/io` to give smooth
  per-second progress) doesn't track SteamCMD's mmap-based
  writes on Linux until the kernel flushes dirty pages from
  the page cache — only catches up in bursts under cache
  pressure — so the bar sat at `0 / N MB (0.0%)` until ~50%
  of the download had elapsed. A new stdout-side parser
  handles SteamCMD's per-second
  `Update state (0xN) PHASE, progress: PCT (BYTES / TOTAL)`
  lines (these don't appear in content_log.txt, only stdout,
  so the previous Windows-only content_log path didn't see
  them). The I/O poller now writes cooperatively — only when
  its derived value is ahead of what's already there — so
  the stdout parser stays authoritative on Linux without a
  platform branch in the code; whichever source is denser
  wins each tick.
- SteamCMD's interactive REPL prompt no longer sticks as
  the post-completion display message. SteamCMD writes
  `-- type 'quit' to exit --` immediately before consuming
  the `+quit` verb; the line was reaching the message
  fallback in the stdout handler and stayed visible for the
  rest of the install-completion display. The fallback now
  skips whitespace-only lines and decoration rows (pure
  `-=_` divider rows, REPL prompts), and the success path
  overwrites the message with `Installation completed.`
  regardless of what the last stdout line was.
- `dotnet publish -r linux-x64` no longer drops a Windows
  `.exe` in the output folder. The two MSBuild targets that
  cross-compile and copy `GSM.CtrlCSender.exe` into the
  Node's publish output now gate on the runtime identifier
  — skipped when `RuntimeIdentifier` is set and doesn't
  start with `win`. Mirrors the existing pattern used for
  `install-service.bat` / `uninstall-service.bat`. When RID
  is unset (legacy framework-dependent publish, no platform
  commitment) the helper is still included on the
  Windows-bias assumption, parallel to the .bat files'
  behaviour.

### Added

- **Install-method-aware installation UI.** The Installation
  panel header now shows "Install method: Steam (SteamCMD)"
  / "Direct download" / "Manual" alongside the install path
  and version, surfacing what was previously implicit. The
  Steam-credential row is hidden in the New Installation and
  Edit Installation forms when the chosen method isn't
  SteamCMD, removing a confusing dead control on direct-
  download or manual installations.
- `DownloadFileStep.StripTopLevelDirectory` flag for plugins
  whose archives wrap every entry under a single top-level
  directory (autotools-style `factorio_2.0.76.tar.xz` → all
  entries under `factorio/`). The node's archive extractor
  detects the wrapper and hoists contents up to the install
  root. Native tar uses `--strip-components=1`; the
  SharpCompress fallback uses a staging-and-hoist pass.
  Defaults to False — existing plugins are unaffected.
- `IVersionAwarePlugin.GetInstalledVersionAsync(config,
  client, cancellation)` — reads the installed version off
  the node's filesystem (via the node's existing file-ops
  endpoints) in the same format `GetLatestVersionAsync`
  returns, so the manager's inequality check between the
  two can detect drift cleanly without false positives from
  synthetic provenance stamps. Called only for non-SteamCmd
  installs (Steam installs continue to use the appmanifest
  ACF buildid path). Currently implemented by Factorio
  (reads `data/base/info.json`); Last Oasis's plugin doesn't
  implement `IVersionAwarePlugin` so the contract change is
  non-breaking for it.

- **Save management tab on Factorio instances.** Lists save files
  on the node with Upload, Download, Delete, Rename, and Copy
  buttons. Streamed uploads/downloads handle 100MB+ saves without
  buffering on either side. The instance config's "Save File"
  field is now a picker dropdown populated from the same listing
  — no more typing exact filenames. Powered by a new opt-in
  `IManagedDirectoriesProvider` plugin interface; plugins that
  don't implement it (Last Oasis) keep their previous three-tab
  layout untouched.
- **"Generate New Map..." tab.** Click the Generate New button on
  the Saves tab to open a Generate Map sibling tab. Pick a preset
  (Default, Death World, Rail World, Ribbon World, Rich Resources,
  Lakes, Island), set a save name and optional uint32 seed, hit
  Generate. The save appears in the Saves tab when the operation
  completes. Powered by a new opt-in `IFileGenerationProvider`
  plugin interface that's generic across any one-off file-
  producing operation, not just maps.
- **"Server Settings" tab on Factorio instances.** Edit the 18
  most commonly-changed `server-settings.json` fields (name,
  visibility, factorio.com auth, game password, /commands
  permissions, auto-pause, autosave settings, AFK kick) without
  opening the JSON. The plugin owns parse/serialise so unknown
  fields a user added by hand outside the form
  (segment_size_*, max_upload_*, etc.) round-trip unchanged on
  Save. A missing file renders with schema defaults and gets
  created on first Save. Powered by a new opt-in
  `IInstanceFileEditorProvider` plugin interface.
- **Node-side file CRUD endpoints** for plugin-declared managed
  directories. Foundation for everything above. Path validation
  rejects `..` traversal and enforces plugin-declared root +
  extension allowlists per request. Uploads stream via
  `Request.Body.CopyToAsync` rather than buffering, so a 100MB
  Factorio save doesn't blow up the node's working set.
- **Pre-flight config validation on instance start.** Plugins'
  `ValidateConfig` hook now runs before every start; warnings
  surface as a warn-and-confirm dialog rather than letting the
  user discover the problem via a crash a few seconds later. The
  canonical case is Factorio with no save selected and "Use
  latest save" off — previously a 30-line stack trace, now a
  one-line MessageBox the user can act on.
- **Custom Discord panel composition.** Panels in the Discord
  bot integration are no longer locked to a fixed row layout.
  Each panel's row composition is configurable from the panel
  editor: pick which elements appear (state icon, instance
  name, state text, player count, next restart, game context
  line, node name, free-text separators), in what order, and
  whether the whole panel is grouped (none, by node, by game,
  by node-then-game). Existing panels render byte-identically
  until edited — the default layout reproduces the prior
  hardcoded format. Stored as JSON on the panel row; new
  `Layout:` and `Group by:` controls in the editor.
- **Per-panel Discord role overrides.** Panels can now define
  their own role-to-permission map that fully replaces the
  guild-default for that panel. Useful when one guild hosts
  multiple games with different ops teams — LO operators get
  Manage on the LO panel without also gaining it on the
  Factorio panel. Configured via a new "Override roles..."
  button on the panel editor. The status hint shows whether a
  panel uses the guild default or has overrides in effect.
  Whole-mapping override (not augmentation): if a panel has
  any overrides, the guild-default is not consulted for that
  panel — enables denial-by-omission.
- **Pagination on the Discord Manage dropdown.** Discord caps
  select-component options at 25; previously the Manage
  dropdown silently hid the rest. Panels with more than 25
  in-scope instances now show "Page X of Y" with prev/next
  buttons. Single-page panels are visually unchanged.
- **Bot connection retry buffer.** The Discord bot's outbound
  notification path used to drop events on transient failures
  (rate limits, network blips, brief disconnects). Failed
  events are now held in a per-destination ring buffer (cap
  100) and replayed on the next worker tick, ahead of fresh
  events so order is preserved. Permanent failures (channel
  deleted, bot lacks permission, malformed payload) still
  drop fast and log loudly. Buffer overflow during a long
  outage drops the oldest events to bound memory.
- **Live bot connection state on the Discord Bot form.** The
  status label now polls once per second and shows uptime
  when connected ("Connected for 2h 18m (since 14:23
  local)."). Previously the label stayed stale at
  "Connecting to Discord…" until the form was reopened.
- The node persists a per-(instance, log file) byte cursor in a new
  `TailerPositions` SQLite table. On instance start, the tailer
  resumes from the saved position when the file's first-256-byte
  fingerprint matches what was saved, otherwise falls back to the
  existing size-based heuristic. This fixes Factorio's chat
  duplication, eliminates the engine-state "Closed → InGame"
  zoom-through on restart, and gives clean resume behaviour after
  Manager restarts while an instance is still running. No effect on
  Last Oasis, which already creates a new log file per run.
- **Status icons on the MainForm tree.** Each node /
  installation / instance entry now carries a coloured shape
  badge encoding its current state. Colour is shared across
  all three tiers — Green = healthy, Yellow = update
  available or version mismatch, Red = unreachable or
  crashed, Blue = working (installing / starting / stopping),
  Gray = unknown / not installed / stopped, DarkRed =
  crash-loop halted — so colour reads independently of tier.
  Shape encodes the tier itself: nodes show a stacked-rack
  server icon, installations show a folder, instances show a
  circle. Refreshes every 2 seconds from cached manager
  state (no extra network polling) and immediately on tree
  rebuild (Add Node, Edit Installation, etc.) so badges are
  current without waiting for the next tick. Bitmaps drawn
  programmatically with GDI+; no external assets to ship.

### Notes

- SharpCompress 0.36.0 doesn't process Pax extended-header
  entries in BSD-tar-produced archives. Linux/macOS tar.xz
  extraction routes around it via native `tar`; Windows
  tar.xz still uses SharpCompress because no plugin
  currently produces a Windows direct-download case. If one
  materialises, modern Windows (10 1803+) ships bsdtar at
  `%SystemRoot%\System32\tar.exe` and the same `tar -xf`
  shell-out works (xz autodetected).
- The `ArchiveFactory.Open` path used for `.tar.gz` / `.7z`
  / `.rar` in the SharpCompress fallback doesn't apply
  unix file modes either. No current users; tracked as a
  follow-up if a future plugin needs executable content
  out of one of those formats.

- Pre-existing duplicate rows in `chat_messages` from prior
  Factorio restarts are not cleaned up automatically. Run
  `DELETE FROM chat_messages WHERE instance_id = '<id>';` against
  the node DB and the equivalent against the manager DB if a fresh
  slate is wanted; otherwise they're cosmetic.
- The very first instance restart after upgrading still replays
  history once because the cursor table starts empty. Subsequent
  restarts are clean.
- Synthetic leaves are persist-only — they don't fire `PlayerLeft`
  notifications. The corresponding `InstanceStopped` /
  `InstanceCrashed` notification already covers the situation, and
  per-player notifications on top of that would spam Discord badly
  when a populated server stops.

- Pre-existing duplicate rows in `chat_messages` from before the
  `ux_chat_dedup` UNIQUE INDEX shipped are not cleaned up
  automatically by the migration that adds the index. The index
  itself only de-dupes going-forward inserts; historical
  duplicates predate it and the index creation succeeds against
  the existing data (INDEX-only constraints don't validate prior
  rows). If a fresh slate is wanted, query for rows duplicated
  by `(instance_id, display_name, text)` and DELETE all but the
  earliest per group. Affects both the node DB and the manager
  DB — they hold separate copies. Optional — the duplicates are
  cosmetic and only visible in History queries that group by
  display name + text without timestamp.

- Orphaned `SessionIdentity` rows from before the manager-side
  `ResolveSessionIdentity` fallback shipped (sessions recorded
  with empty identity during the adoption window when the
  4-line tile-load sequence had rotated out of the node SSE
  ring buffer) can be retroactively rebased via a SQLite 3.33+
  UPDATE FROM joining `SessionHosts` on the `(InstanceId,
  HostedFromUtc..HostedUntilUtc)` range. Optional — affects only
  historical session-grouped queries ("show me all chat from
  the realm that hosted tile X yesterday"), not live operation.
  Going-forward rows pick up the correct identity through the
  fallback chain.

## [0.1.0] - 2026-05-02

First named version. Establishes the versioning baseline; everything
listed here was built incrementally before this point and is captured
in one section as a one-time backfill. Future releases will only list
deltas relative to the previous version.

### Added

#### Architecture

- Three-project solution: `GSM.Contracts` (shared interfaces and DTOs,
  no NuGet dependencies), `GSM.Node` (ASP.NET Core Minimal API service
  that runs on game-server machines), and `GSM.Manager` (WinForms
  desktop app). Plus `GSM.CtrlCSender` (Windows console-control helper)
  and `GSM.NodeSetup` (cross-platform installer).
- Manager-interprets / Node-executes split: plugins run only on the
  Manager and send plain data to Nodes. Plugin interfaces live in
  Contracts so Roslyn-compiled plugin source can reference them
  without depending on the Manager executable.
- Build versioning via `Directory.Build.props`. Protocol and contracts
  versions tracked separately in `NodeApiContract.vb`. See
  [VERSIONING.md](VERSIONING.md).

#### GSM.Node — game-server agent

- ASP.NET Core Minimal API host with bearer-token authentication, per-IP
  rate limiting, and per-IP auth-failure lockout middleware.
- `ProcessManager`: spawns game-server processes with redirected stdio,
  manages their lifecycle, drains output streams to prevent UE4 pipe
  blocking, and handles graceful shutdown.
- `RingBufferStore`: per-instance log ring buffer with subscription-based
  streaming via Server-Sent Events for the Manager log viewer.
- `EventStore`: applies declarative regex-based log parse rules from
  plugins; tracks per-instance player list and server state in memory;
  persists chat messages to SQLite. Manager can connect at any time and
  see current state without having been running during the events.
- `RconClientManager`: source RCON protocol client with reconnect logic.
- `InstallRunner`: SteamCMD integration with Steam Guard prompt flow,
  exit-code interpretation (5 = guard required, 7 = post-install
  self-update success), redistributable install pass (`vc_redist`,
  `dxsetup`), tar.xz/tar.gz/7z/rar extraction via SharpCompress.
- File-based log tailing alongside stdout capture, with open/read/close
  per poll cycle to coexist with the game's exclusive write handle.
- Crash detection and restart policy enforced node-side so restarts
  work even when the Manager is offline.
- Windows service deployment via `install-service.bat`/`uninstall-service.bat`.
- Console-control-event isolation: process-local handler that swallows
  CTRL_C_EVENT so the helper-fired CTRL_C reaches game children without
  also tearing down the node host.

#### GSM.Manager — desktop control plane

- WinForms application with EF Core SQLite persistence and migrations
  run via the Visual Studio Package Manager Console.
- `PluginRegistry`: hot-reload Roslyn compilation of plugins from
  `.vb` source files in the Plugins folder. Each file compiles as its
  own assembly so a single broken plugin doesn't block others. Orphan
  detection surfaces installations or instances whose plugin disappeared.
- `NodeHttpClient` + `NodeHttpClientFactory`: typed HTTP client per node
  with bearer-token auth, retry-on-transient policies, and Server-Sent
  Events log streaming.
- `CredentialService`: Steam credential storage encrypted with Windows
  DPAPI.
- `InstanceManager`: instance lifecycle (start/stop/restart), live state
  refresh poller, log-stream reconnect on Manager restart.
- `InstallationManager`: install/update orchestration including the
  Steam Guard prompt round trip back to the user.
- `NotificationService` + `NotificationEmitter`: pluggable notification
  pipeline. Includes built-in Discord webhook plugin with custom embed
  rendering, token substitution (`{RuleName}`, `{InstanceName}`,
  `{NodeName}`, `{Time}`, `{Date}`, etc.), and 1:1 destination targeting
  via `IDestinationTargetingPlugin`.
- `AutomationEngine`: declarative rules with five scopes, four trigger
  types, and eleven leaf actions; cron timers via NCrontab; condition
  evaluation with three condition types; reorderable sequence steps.
- `RestartCoordinator`: tile-loaded ready-signal handling for staggered
  multi-instance restarts.
- `VersionCheckService`: 60-minute polling per installation, raises
  `VersionMismatch` events for rules that subscribed.
- `ChatRetentionPruner`: idempotent background pruner for the chat
  history table.
- `RuleEditorForm`, `ConditionEditorForm`, `StepEditorForm`,
  `TemplateEditorForm`, `VisibilityProfileEditorForm`,
  `NotificationsForm`, `HistoryWindow`, `PluginStatusForm`,
  `SteamCredentialsForm`, `RealmCredentialsForm`, `SettingsForm`,
  `NewInstallationForm`, `EditInstallationForm`, `EditInstanceForm`,
  `AddInstanceForm`, `NodeSetupForm`, `LogViewerForm`,
  `AutomationRulesForm`.
- MainForm tree (Nodes → Installations → Instances) with humanised
  Automation Rules listview: live "Running... (12s)" / "Ran 2m ago" /
  "Skipped 5s ago" Last Run column, display-name substitution for raw
  GUIDs in execution history.
- File logging on both Manager and Node: daily rotation, 30-day
  retention, framework chatter clamped to Warning to keep volume sane.

#### Plugins (loaded at runtime via Roslyn)

- `LastOasisPlugin`: realm-aware Last Oasis dedicated server support
  with CustomerKey/ProviderKey held at the installation level and
  optional per-instance overrides for multi-realm hosts. Includes
  `SteamCmdInstallMonitor` for tile-binding readiness signals.
- `FactorioPlugin`: Factorio dedicated server with mod management,
  declarative log-parse rules for player join/leave and chat.

#### Tooling

- `GSM.CtrlCSender`: tiny Windows console helper used by the Node to
  deliver `CTRL_C_EVENT` to UE4 game-server children. Published
  self-contained-single-file in production so it works inside a Node
  publish folder that has no shared framework.
- `GSM.NodeSetup`: cross-platform installer with Windows-only WinForms
  GUI (gated behind `WINDOWS_GUI` compile constant) and Linux-friendly
  console fallback. Post-publish target deploys it next to the Node
  binary.
- About dialog (`Help → About`) showing build version, contracts
  version, and protocol version. Status-bar version indicator on the
  main window.

### Notes

This is an internal-baseline release. Sharing with external users is
not intended for 0.1.x; the immediate motivation for naming this
version is to establish the versioning, changelog, and release-process
groundwork before the first external user arrives.

<!--
  Comparison and tag links go here once phase 5f-4 stands up
  the GitHub Actions release workflow and the repo's public URL
  is settled. Form: [0.1.0]: <repo-url>/releases/tag/v0.1.0
-->
