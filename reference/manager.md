# PowerGSM Reference — Manager

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
The Manager is the WinForms control plane: installation / instance config
and editor UI, live-state refresh, the Manager↔Node log-stream plumbing,
session History (timeline + window), the Phase 4C configuration-file
management surface (saves, file generation, structured config editor),
Phase 5m resilience (tray, safe mode, orphan guard), and the 5l
self-update flow. The Node-side wire API it consumes is in
[`node.md`](node.md); plugin, automation, and identity material live in
their own files; Manager-relevant VB.NET pitfalls are in
[`vbnet-gotchas.md`](vbnet-gotchas.md).

> Note: Phase 4C's node-side file-operations sub-phase (4c-1) lives in
> [`node.md`](node.md); this file covers the Manager-side 4c-2 / 4c-3 / 4c-4.

---

### Installation config persistence & edit UI

- `InstallationEntity` has a `ConfigJson` field that persists install-level
  config values (e.g. Last Oasis CustomerKey/ProviderKey — stored once,
  shared by all instances of that installation).
- `InstallationEntity.SteamCredentialId` associates a Steam credential
  with an installation so updates reuse it automatically.
- `EditInstallationForm` in RemainingForms.vb edits the display name and
  all install-level config fields via SchemaFormBuilder on
  `plugin.GetInstallConfigSchema()`.
- MainForm "Edit Installation..." context menu opens this form.

---

### Instance config merge

In `InstanceManager.StartInstanceAsync`, installation ConfigJson is
merged into instance CustomFields before the plugin is invoked:

1. Load `installEntity.ConfigJson` into a case-insensitive dict
2. Overlay `instanceEntity.ConfigJson` on top (instance overrides
   installation)
3. Pass merged dict as `InstanceConfig.CustomFields` to the plugin

This is how Last Oasis's CustomerKey/ProviderKey (stored on the
installation) reach `BuildLaunchArguments`. Instance-level overrides
for those same keys work too — useful when one installation hosts
multiple realms.

---

### Live state refresh in the UI

- `InstancePanel` has a 3-second `_refreshTimer` polling
  `InstanceManager.RefreshInstanceStateAsync(instanceId)` to keep the
  status label live (Running/Starting/Stopped/Crashed with PID or exit
  code). Maps all 8 `InstanceState` values to colored labels.
- `LogViewerForm` has a 500ms refresh timer polling the manager ring
  buffer with timestamp-based cursor (`_lastSeenTimestamp`). Batched
  append via `WM_SETREDRAW`-suspended RichTextBox to avoid per-line
  scroll thrash under high throughput. Trims to 4000 lines when buffer
  exceeds 5000.
- Log viewer reloads history from the node on open via
  `GET /api/instances/{id}/logs/recent` so post-Manager-restart views
  aren't empty.
- `InstanceManager.EnsureLogStreamAsync(instanceId)` reconnects a
  stream if one isn't active — called when the log viewer opens and
  from `ReconnectLogStreamsAsync` at Manager startup.

---

### Install-method-aware UI

Three small UI changes around install-method visibility:

- `NewInstallationForm` hides the Steam-credential dropdown
  (label + combo) when the chosen install method isn't
  `SteamCmd`. Captured via `_steamCredLabel` field plus a
  `_methodComboBox.SelectedIndexChanged` handler that toggles
  `Visible` on both controls. Force-resets the combo to index
  0 (Anonymous) when the method changes away from Steam, so
  switching back doesn't leave a stale credential selection.
- `EditInstallationForm` (`RemainingForms.vb`) does the same
  hide-on-non-Steam pass on form load — one-shot rather than
  reactive because the install method isn't editable
  post-creation. Promoted the previously-local `credLbl` to
  a `Private _credLabel As Label` field so the visibility
  toggle has access to it.
- `InstallationPanel` header (`UiPanels.vb`) renders an
  `Install method:` line between the path and version. The
  Steam-account credential label is only shown for SteamCmd
  installs. Header height grew from 150 to 170px to fit the
  new line; subsequent labels shifted +20px in y to match.

---

### Engine output dialog for file-generation failures

`FileGenerationPanel.ApplyFailureState` previously rendered the
failure summary into the single-line status label. When the
bare error message exceeded 80 characters (the typical case —
"Process exited with code 1 (expected 0): /opt/PowerGSM/..."
is well over that), the captured engine output (which the node
did populate via `GenerateMapResponse.Output`) was dropped
from the display, leaving the user with no diagnostic context.

The panel now opens a resizable dialog whenever a generation
failure has non-empty captured output. Layout matches
`NewInstallationForm.ShowInstallErrorDialog` for visual
consistency: warning icon + bold headline + `Engine output:`
label + multiline read-only `TextBox` scrolled to the end
(engine errors land at the bottom of the output, after a
banner of init lines that aren't actionable) + OK button.
Minimum size 480×280, default 720×480, fully resizable.

Reused for any future plugin-driven file-generation operation
that fails with engine output — the panel itself is generic
(`IFileGenerationProvider`-driven) and not Factorio-specific.

---

### UI preference persistence patterns

Two scopes used across `InstancePanel` and
`InstallationPanel`:

**Per-entity** (e.g., Show Logs toggle on InstancePanel,
keyed by InstanceId): class-shared
`ConcurrentDictionary(Of String, Boolean)`. Each user
toggle writes the dict; an `OnLoad` override reads the
saved value and applies it. Guarded by a `_restoringShowLogs`
flag that suppresses the echo write-back in the toggle
handler AND the auto-select-Logs-tab side effect in
`ShowLogsTab`.

**Per-panel-type** (e.g., last-selected tab on both
InstancePanel and InstallationPanel): two separate
`Private Shared` String fields (one per panel class)
storing the last-selected tab's `.Text`.
`SelectedIndexChanged` handler hooked AFTER the initial
tab Add calls in `InitializeControls` — the synthetic
`-1 → 0` event that fires on the first Add would otherwise
pre-write the default tab name. Identity by `.Text` not
index because dynamic tabs (Logs, plugin-supplied managed-
files / editor tabs, Progress tab on InstallationPanel)
shift indices across panels. Tabs that exist on only some
panels fall through cleanly to the default selection when
the saved name doesn't match.

Both patterns share key mechanics: `OnLoad` rather than
the constructor (so `Me.BeginInvoke` works — the handle
isn't created until parenting); a `_restoring...` Boolean
flag to suppress side effects during restore; manager-
restart scope by design (fresh session starts on defaults).
Per-instance vs per-panel-type is a deliberate choice per
preference: Show Logs is per-instance because log-watching
is instance-specific; tab is per-panel-type because the
user wants "compare configurations across instances" to
keep them on Configuration.

The per-panel-type tab persistence removed about 80–90% of
the navigation clicks involved in comparing configurations
or logs across instances during live operation, per
May 2026 user feedback. Worth knowing as a baseline cost-
benefit data point for any future "is this UX feature
worth it" question on similar persistence patterns.

---

### Manager-side log stream idempotency

`InstanceManager.StartLogStream` is idempotent under a
private `_logStreamLock` SyncLock. Before installing a new
cts, it `TryRemove`s any existing entry from
`_logStreamCancellations`, calls `Cancel()` + `Dispose()`
on it, and clears the stale `_logParsers` entry. The
orphaned task's existing compare-and-remove in its Finally
block bails correctly when it sees a mismatched cts in the
dict. `Task.Run` is INSIDE the lock so parser registration
in `_logParsers` happens before the streaming task starts —
otherwise the new task could read lines while a previous
parser is still registered.

Two callers race here under normal operation:
`StartInstanceAsync`'s success path and
`BackgroundPollLoopAsync`'s stream-health check. The
background poll runs every 3 seconds and observes whichever
state `_liveStates` has at the moment it reads. Between
`_liveStates(id) = result` (Running) and `StartLogStream(...)`
in the start path, the dict slot is briefly empty — if the
poll's stream-health check runs in that window, it also
calls `StartLogStream`. Pre-fix the dict assignment was a
naked upsert with no cancellation of the orphaned cts, and
both background tasks ran forever in parallel, producing
permanent every-line-doubled output for the rest of the
instance's session. This was the headline bug behind the
user-reported "logs doubling after restart" symptom.

---

### Manager parser state vs node EventStore

Node-side `EventStore` rules are STATELESS line-by-line
matchers — each rule's regex runs against each line in
isolation, no cross-line state. This is what makes chat
dedup work cleanly across adoption replay: the replay
re-runs ProcessLine against the same lines and produces
the same persistence calls, dropped by `INSERT OR IGNORE`.

Manager-side log parser is DIFFERENT — it has STATEFUL
sequences. The Last Oasis tile-load identity is committed
by a 4-line sequence (`Started hosting tile` → realm_id →
tile_name → tile_id), and the in-memory parser state
threads context across those four lines until all are
seen. On adoption, that sequence can be hours old and has
rotated out of the node SSE ring buffer (4096 lines), so
the manager parser comes up with `CurrentSessionIdentity =
Nothing` and any chat / player-activity rows persisted in
that window would orphan from session context.

Solution: DB-as-source-of-truth fallback.
`InstanceManager.ResolveSessionIdentity(instanceId)` walks
a lookup chain: parser-committed identity (live path,
unchanged) → in-memory cache (`_adoptedSessionIdentities`)
→ SQLite query against `SessionHosts WHERE InstanceId = ?
AND HostedUntilUtc IS NULL ORDER BY HostedFromUtc DESC
LIMIT 1` for the most recent open hosting record →
synthesized `{gameId}:{instanceId}` if nothing matches.
Self-healing: parser commit invalidates the cache, future
lookups bypass the DB. Cache is dropped on instance stop
via `ClearPlayerTracking`.

General pattern: two paths reading/writing the same
logical state at different moments without atomicity — or
with different state-of-truth assumptions — is a recurring
bug shape across the manager-node boundary. Solutions are
either (1) collapse the two ops into one atomic operation
(SubscribeAndGetTail, StartLogStream SyncLock) or (2)
rehydrate from DB-as-source-of-truth lookup (SessionIdentity
fallback). Pick based on which side of the boundary owns
the authoritative state.

---

### History timeline integrity

Two pre-1.0 bugs fixed in the same arc, both manifesting as
**missing rows** in the History window even though the underlying
event reached the node correctly. Documented together because
both are about reconciling node-authoritative state into the
manager's EF mirror, both surface only after specific timing
conditions, and both bit during the same realistic test session.

#### Bug 1 — Synthetic player-leave on instance stop or crash

When an instance stopped or crashed with a player still online,
the History timeline showed the player's Join with no matching
Leave. Symptom: timeline ended on a join, the player
"disappeared" from the user's mental model with no closure.

Root cause: the manager's per-instance `_activePlayers` HashSet
(populated by `HandlePlayerJoin` / `HandlePlayerLeave` from the
log stream) was emptied on stop via `ClearPlayerTracking` —
but the old implementation just dropped the bucket without
emitting any leave events for the still-tracked names. The node
side doesn't help: when the process exits, no leave log line
ever gets written, so there's nothing for the parser to see.
The player's join was persisted to `PlayerActivity`, but the
matching leave only existed in the manager's in-memory bucket
and vanished with it.

Fix in `GSM.Manager\Core\InstanceManager.vb`:

- **`ClearPlayerTracking` rewritten to flush.** Drains the
  bucket atomically (TryRemove + SyncLock + ToList + Clear),
  then for each name calls `PersistPlayerObservation(instanceId,
  name, isJoin:=False)` wrapped in try/catch so one DB error
  doesn't lose the rest. Logs `"Flushed {Count} player(s) as
  synthetic leave on stop for {Id}"` at Information level.
  **Persist-only** — does NOT fire `PlayerLeft` notifications.
  The `InstanceStopped` / `InstanceCrashed` notification
  already covers the scenario, and per-player notifications on
  top of that would spam Discord when a populated server stops.
- **Order swap in `StopInstanceAsync.Finally`:**
  `ClearPlayerTracking` BEFORE `StopLogStream`, not after. The
  flush calls `ResolveSessionIdentity`, which reads the
  parser's `CurrentSessionIdentity` from `_logParsers`.
  `StopLogStream` removes that parser entry, after which the
  resolver falls back to `{gameId}:{instanceId}`. For Last Oasis
  that fallback differs from the real `lastoasis:realmId:tileId`
  session identity the joins were stamped with, so flushing
  AFTER `StopLogStream` would orphan the synthetic leaves
  under a different SessionIdentity than the matching joins.
  Factorio's fallback happens to match its real format, so the
  Factorio path was correct either way — but cheap to get
  right for both regardless.
- **Terminal-state detector in `RefreshInstanceStateAsync`.**
  Catches the crash and crash-loop paths where
  `StopInstanceAsync.Finally` (which also calls
  `ClearPlayerTracking`) wasn't the path that took the instance
  down. Compares `previous.CurrentState` to `result.CurrentState`;
  if `newState` is terminal (Stopped / Crashed / CrashLoopHalted)
  AND `prevState` was not, fires the flush. **Idempotent** —
  a user-initiated stop flushes via the Finally first, and this
  callsite then sees an empty bucket and no-ops. Wrapped in
  try/catch so a flush exception can't cascade into the
  notification-emitting branch above it. Doesn't depend on
  `_emitter` being non-null — the flush is about persistence,
  not notifications.

#### Bug 2 — Chat mirror DateTimeKind round-trip

After a manager restart, chat messages persisted to the node
failed to mirror into the manager's `ChatMessages` table.
Symptom: Chat tab on the manager showed the message (queries
the node directly), but the History window's timeline didn't
(queries the manager mirror). User-visible diagnostic: open
`gsm.db` in DB Browser, run `SELECT * FROM ChatMessages WHERE
InstanceId = '<id>'` — the missing row is genuinely absent.

Root-cause chain:

1. JSON deserialisation of the node's `/api/instances/{id}/chat`
   response: `ChatMessage.TimestampUtc` arrives with `Kind=Utc`
   (System.Text.Json parsing the node's `Z`-suffixed timestamp).
2. Manager stores `ChatMessageEntity.TimestampUtc` via EF Core.
   **EF Core's SQLite provider stores `DateTime` as TEXT in
   `yyyy-MM-dd HH:mm:ss.fffffff` format — no offset, no Z
   suffix — and reads it back with `Kind=Unspecified`.** The
   kind information is unrecoverable from the storage format.
3. After a manager restart, in-memory `_chatCursors` is empty.
   `MirrorChatForInstanceAsync` calls `SeedChatCursor`, which
   returns `db.ChatMessages.Max(c.TimestampUtc)` — Kind=Unspecified.
4. `NodeHttpClient.GetChatHistoryAsync` serializes the cursor
   via `sinceUtc.Value.ToString("o")`. For Kind=Utc that
   produces `2026-05-03T00:03:57.0000000Z`. For Kind=Unspecified
   it produces `2026-05-03T00:03:57.0000000` — no Z.
5. The node endpoint parses with `DateTimeStyles.RoundtripKind`
   → keeps `Kind=Unspecified`, then calls `parsed.ToUniversalTime()`.
   **`ToUniversalTime()` on `Unspecified` treats it as Local
   time and shifts by the host's UTC offset.**
6. For a user in Cicero IL (UTC-5 in May), a cursor of
   `00:03:57 Unspecified` becomes `05:03:57 Utc` on the node
   side. The SQL `WHERE timestamp_utc > '...05:03:57Z'` then
   excludes any chat whose actual UTC timestamp is between
   `00:03:57` and `05:03:57` — five hours of silently-dropped
   messages.

The bug self-corrects after one successful mirror (newCursor
from JSON has Kind=Utc), but it PREVENTS successful mirrors,
so the cursor stays Unspecified across the manager session.
Triggers on every manager restart with chats persisted between
restarts. Not surfaced earlier because dev iteration in a tight
rebuild-test loop usually keeps chats in a single manager session
or restarts the instance (clearing node DB chat for the new
run), so the cross-restart case is the one that bites real
users first.

Fix is defense-in-depth at both ends:

- **`GSM.Manager\Core\InstanceManager.vb` — `SeedChatCursor`.**
  Final return is `DateTime.SpecifyKind(latest.Value,
  DateTimeKind.Utc)`. The column is named `TimestampUtc` and
  is always written from `DateTime.UtcNow`, so the
  metadata restoration isn't a guess — it's reasserting an
  invariant the storage format dropped. Long XML-doc comment
  on the function captures the full chain so future readers
  don't have to re-derive it.
- **`GSM.Node\Endpoints\InstanceEndpoints.vb` —
  `/api/instances/{id}/chat` endpoint.** Replaces
  `parsed.ToUniversalTime()` with a `Select Case parsed.Kind`:
  Utc → as-is; Local → ToUniversalTime; Unspecified →
  SpecifyKind(parsed, Utc). The contract is that an offset-less
  ISO string in this parameter means "this is a UTC value, the
  sender just didn't put a Z on it" — the parameter is named
  `since` against a column called `timestamp_utc`. Even with
  the manager-side fix, this stricter parsing is cheap defense
  for any future caller that sends offset-less timestamps.

**Recovery path:** chats persisted to the node during the bug
window are still in the node's `chat_messages` table — the bug
was a filter on the read side, not a loss on the write side.
After rebuilding the manager with the fix and restarting it,
the next mirror cycle re-seeds the cursor (now Utc-kind), the
request hits the node with a Z-suffixed `since`, and any chats
more recent than the last successfully-mirrored row come
through on the next poll. **No manual SQL or DB cleanup
needed** — recovery is automatic on the next manager start.
The Manager's other tabs aren't affected: the live Chat tab
queries the node directly and never went through the broken
path; the History window's chat rows show up as soon as
the mirror catches up.

**Files modified:**
- `GSM.Manager\Core\InstanceManager.vb` — `ClearPlayerTracking`
  rewritten to flush bucket as synthetic leaves;
  `StopInstanceAsync.Finally` order swap;
  `RefreshInstanceStateAsync` terminal-state detector;
  `SeedChatCursor` UTC-kind tagging
- `GSM.Node\Endpoints\InstanceEndpoints.vb` —
  `/api/instances/{id}/chat` endpoint Kind-aware `since`
  handling

---

## ROUND D — Session history & UI polish

### Session history persistence

Three tables track player and session history across the lifespan of
sessions, orthogonal to chat retention:

- **PlayerSessions** — aggregate summary per (SessionIdentity, PlayerName).
  First/last seen timestamps, LastHostInstanceId. Upserted on every
  join/leave observation.
- **PlayerActivity** — per-event stream. Every join and leave produces
  a row; powers the timeline view in the History window.
- **SessionHosts** — records which instance hosted which session, and
  when. Opens on TileLoaded, closes on TileUnloaded or instance stop.
  Includes TileName (populated from plugin-supplied Metadata when
  available).

**Retention model:** time-scoped data (ChatMessages) gets pruned on a
configurable `ChatRetentionDays` setting (default 90). Identity-scoped
data (PlayerSessions, PlayerActivity, SessionHosts) is never
time-pruned — it persists until the underlying session identity goes
away (e.g. realm reset).

**Session identity format:**
- Last Oasis parser produces `"lastoasis:{realm_id}:{tile_id}"` via
  its CurrentSessionIdentity property
- Fallback for games without migration semantics: `"{gameId}:{instanceId}"`

`InstanceManager.ResolveSessionIdentity(instanceId)` centralises this
resolution; `GetCurrentSessionIdentity(instanceId)` is the public
wrapper used by the UI (e.g. the History button on InstancePanel).

### HistoryQueryService

`GSM.Manager\Core\HistoryQueryService.vb` — singleton registered in
ManagerProgram DI. Two query surfaces:

- `QueryTimelineAsync(filter)` — returns chronological event rows
  across ChatMessages + PlayerActivity, filtered by SessionIdentity,
  PlayerName, and UTC time range. Powers the History window's
  timeline tab.
- `QuerySnapshotAsync(instantUtc, filter)` — returns who was online
  at a specific instant by replaying PlayerActivity up to that
  timestamp. Powers the "snapshot at instant" tab.

`FormatSessionLabel(sessionIdentity)` produces human-friendly labels
("LO realm Site-Main / Tile 5 / 2026-04-21 19:23") by joining
SessionHosts and the earliest PlayerActivity row. Used throughout the
History window so users never see raw "lastoasis:uuid:uuid".

### HistoryWindow

Non-modal Form registered via MainForm's Tools → History menu AND from
`InstancePanel.OnOpenHistory()` (launched by the per-instance "History"
button, which pre-fills the filter with the instance's current session
and a recent time range).

**UTC / local time toggle:** "Use UTC" checkbox at the top (defaults
to local). `PickerToUtc()` helper uses `DateTime.SpecifyKind` so
pickers produce unambiguous UTC values; `FormatDisplayTime()` converts
on display. The toggle operates on cached query results in-place
(`_lastTimelineResult`, `_lastSnapshotRows`, `_lastSnapshotInstantUtc`)
— no re-query on toggle, just re-render. Pickers default to `DateTime.Now`.

### FormIconHelper (branding consistency)

`GSM.Manager\UI\FormIconHelper.vb` — module with:

- `ApplyTo(form As Form)` — sets the PowerGSM icon on any Form. Silent
  no-op on failure (never let icon load break UI construction).
- `GetLargeBitmap() As Bitmap` — returns a 256×256-or-largest bitmap
  variant of the icon for use as a logo. Caller owns the Bitmap and
  must dispose it.

Resource name: `PowerGSM.ico`. Stream resolved via
`GetType(FormIconHelper).Assembly.GetManifestResourceStream` — works
on modules because the underlying type is NotInheritable Shared.

**Applied to all 16 Forms in the Manager:** MainForm (replaced inline
icon code), NodeSetupForm, NewInstallationForm, NotificationsForm,
TemplateEditorForm, VisibilityProfileEditorForm, HistoryWindow,
PluginStatusForm, SteamCredentialsForm, SteamCredentialEditForm,
RealmCredentialsForm, AutomationRulesForm, RuleEditorForm,
SettingsForm, AddInstanceForm, EditInstanceForm, EditInstallationForm,
LogViewerForm.

**NOT applied to UserControls** (InstancePanel, InstallationPanel,
etc.) — `ApplyTo` takes a Form, so a UserControl would be a type
mismatch. Careful on edit operations that match UserControl
constructors structurally — I've caught this once; revert and target
the specific Form constructor.

### WelcomePanel logo redesign

`WelcomePanel` (in UiPanels.vb) rewritten to display a 128×128
PictureBox at (20, 20) showing the large icon via
`FormIconHelper.GetLargeBitmap()`. Title "PowerGSM" at (170, 40),
subtitle "Game Server Manager" at (170, 85) — both left-aligned
to the same X. Info text at (22, 170) clears the logo vertically.

`Dispose(disposing)` override disposes the PictureBox's Image when
the panel is swapped out of the content area — otherwise every
navigation back to the Nodes root would leak another bitmap copy.

### Settings form rewrite (retention UI)

`SettingsForm` in RemainingForms.vb rewritten with real content:

- **Data Retention section** — NumericUpDown for chat retention days
  (1–3650, default 90), with helper text clarifying that identity-scoped
  data (PlayerSessions, PlayerActivity) is never time-pruned.
- **Paths section** — read-only labels showing resolved full paths for
  `gsm.db` and `Plugins\` directory. `ResolveFullPath` helper wraps
  `IO.Path.GetFullPath` in try/catch.
- **Save / Cancel** with `AcceptButton`/`CancelButton` wiring. Save
  writes to `AppSettings` via `db.SetSetting(SettingKeys.ChatRetentionDays, ...)`.

No new wiring in `ManagerProgram` — `ChatRetentionPruner` already
re-reads the setting on every hourly pass, picks up changes within
the hour.

**VB gotcha learned here:** inside an interpolated string's `{...}`
hole, the expression is normal VB. Don't double-up quotes around
string literals. `$"Database: {ResolveFullPath("gsm.db")}"` is
correct; `$"Database: {ResolveFullPath(""""gsm.db"""")}"` is not.

---

## PHASE 4C — Configuration UI, saves, and file generation

Multi-phase rework of how users interact with the runtime files an
instance reads (server-settings.json, save files, map-gen-settings).
End state: a Factorio instance has Saves, Generate Map, and Server
Settings tabs in its panel; a save file is a one-click upload, a new
map is a one-click generate, and editing the server name doesn't
require SSH'ing to the node.

Four opt-in plugin interfaces drive everything visible. Plugins that
implement them get the new tabs; plugins that don't (Last Oasis,
for now) are completely unaffected — same three-tab layout it had
before the phase started.

### Design decisions locked upfront

Full rationale lives in `Phase4c_Plan.md` (D1–D6). One-line summary
for each:

- **D1 — file is truth at view/edit time.** No DB caching of
  runtime config files; manager fetches fresh from the node on
  open, writes back on save. Out-of-band edits survive.
- **D2 — saves and runtime configs are install-scoped.** Live in
  `<install>/saves/`, `<install>/server-settings.json`. Per-
  instance scoping reserved via `{InstanceId}` token in path.
- **D3 — map gen is a sibling tab to Saves, not modal.** "Generate
  New..." button on Saves opens a tab; user can monitor logs or
  edit other config while the operation runs.
- **D4 — visibility checkboxes raw, not synthesised.** Original
  spec called for an "auth method" radio; shipped form is flat
  with `[Section]` description prefixes.
- **D5 — hardcoded presets, not data-driven.** 7 presets ship as
  string constants in `FactorioPlugin.vb`. Drift risk documented;
  schema-driven custom presets are a v2 follow-on.
- **D6 — stream uploads, no cap.** Request body streamed to disk
  via `CopyToAsync`. 100MB+ Factorio saves work without buffering.

---

### Phase 4c-2 — Saves UI + ManagedFilePicker

First user-visible delivery. Lists files on the node, picks saves
from a dropdown, validates configs before launch.

**Scope narrowed from the original plan.** Phase4c_Plan.md called
for a full `StructuredConfigSchema` with sections, nested groups,
`VisibleWhen` expressions, and StringList/IntegerList field types.
What shipped is a single new `ConfigFieldType.ManagedFilePicker`
value plus a 3-arg overload on `SchemaFormBuilder.Build` that
accepts a file-list provider. Reasoning: the flat
`ConfigFieldDescriptor` schema turned out to cover every use case
4c needed (save selection, server-settings editing) without
introducing a parallel system. Section headers / nested groups
stayed in the v2 follow-on bin.

**New file `GSM.Manager\UI\ManagedFilesPanel.vb`** — the file
management UserControl. ListView with one row per file (name,
size, modified time), button column with Upload / Download /
Delete / Rename / Copy. Resolves the node client + install path
at the start of every operation rather than caching, so a
node-config edit takes effect on the next click without panel
rebuild.

**New `ConfigFieldType.ManagedFilePicker`** field type plus two
descriptor properties: `ManagedDirectoryRef` (which directory
to list) and (existing) `IsRequired`. `SchemaFormBuilder` renders
it as a `ComboBox` with `DropDownStyle = DropDown` (free-form
text allowed) and `AutoCompleteMode = SuggestAppend` so typing
narrows the list. Free-form is intentional — a user can type
the name of a save they're about to upload, or a save listed by
an SCP they did out of band. The `ValueExtractor` reads the
combo's `.Text` (not `.SelectedItem`).

**`SchemaFormBuilder.Build` grew a 3-arg overload** taking a
`Func(Of String, Task(Of IReadOnlyList(Of String)))` file-list
provider. The form-build loop calls it once per
ManagedFilePicker field on a background thread and re-marshals
back to populate the combo. Doesn't block form construction —
the combo is fully usable for free-text entry while the listing
is in flight; only the dropdown items arrive late. The 2-arg
overload still works for read-only Configuration tabs that don't
have a node connection.

**Async-population gotcha:** the lambda that calls the provider
and fills the combo can't be a multi-line async lambda —
VB.Net infers `Task(Of Object)` and complains about "doesn't
return value on all paths". Extracted to a named
`Private Async Function PopulateManagedFilePickerAsync(...)
As Task` per the existing gotcha-table guidance.

**`InstancePanel.BuildManagedFilesTabs`** in `UiPanels.vb` — builds
one tab per declared managed directory between Configuration and
Chat, so display order is `Overview | Configuration | [managed
dirs...] | Chat`. Uses `_tabs.TabPages.IndexOf(_chatTab)` as
insertion anchor. No-op when the plugin doesn't implement
`IManagedDirectoriesProvider`. `{InstanceId}` token substitution
happens here on the manager side per the contract — plugins
return literal tokens and never see the substituted form.

**Pre-flight `ValidateConfig` hook on instance start.** Existing
`IGamePlugin.ValidateConfig(config)` method that previously had
no caller now runs in `InstancePanel.OnStartInstance` BEFORE
`InstanceManager.StartInstanceAsync`. Returned warnings surface
as a warn-and-confirm `MessageBox` ("Start anyway?"); user can
click through. The merge logic builds a case-insensitive dict
from install ConfigJson + instance ConfigJson (instance overrides
install) so the plugin sees the same merged view it sees at
runtime. Failures in the validation lookup itself fall through
to a normal start — we don't want a transient DB error to brick
the Start button. Canonical use case: Factorio with
`UseLatestSave = false` and no `SaveFile` set — the engine
crashes immediately with `"File save.zip does not exist"`, which
is impenetrable to a user; the warning explains it in one line.

**Factorio plugin updates:** `SaveFile` field on the instance
config schema is now `ManagedFilePicker` with
`ManagedDirectoryRef = "saves"`. `GetManagedDirectories` returns
one entry for `saves/` with `Read|Write|Delete` permissions and
`AllowedExtensions = {".zip"}`. `ValidateConfig` returns a
warning when neither `SaveFile` nor `UseLatestSave=true` is set.

### Phase 4c-3 — Generic file generation

Generalised version of what the plan called "Phase 4c-5 map
generation." Schema-driven, plugin-defined, runs against an
instance's install dir for any one-off file-producing operation.
Map generation is the first — and so far only — use case, but
the contract carries no map-specific assumptions.

**Renamed during implementation:** the original
`IMapGenerationProvider` interface became `IFileGenerationProvider`
before shipping, when it became clear the contract shape
(plugin's schema + plugin's step-list) applies generically. Wire
DTOs (`GenerateMapRequest`, `GenerateMapResponse`, endpoint URL
`/api/instances/{id}/generate-map`) kept their original names
for back-compat with already-deployed nodes — a `NAMING NOTE`
comment block in `NodeApiContract.vb` explains. Read those names
as "GenerateFile."

**New contracts in `GSM.Contracts\IGamePlugin.vb`:**

- `IFileGenerationProvider` opt-in interface, five methods:
  `GetTargetDirectoryRef()` — which managed directory the output
  belongs in (used by `ManagedFilesPanel` to decide whether to
  show the "Generate New..." button on its tab);
  `GetButtonLabel()` / `GetTabTitle()` — user-facing strings;
  `GetGenerationSchema(config)` — returns a flat
  `ConfigFieldDescriptor` list rendered by `SchemaFormBuilder`;
  `BuildGenerationSteps(values, config)` — returns a
  `GenerationStepBundle` from the user's filled-in form values.
- `GenerationStepBundle` data class: `Steps As List(Of InstallStep)`
  (only `WriteFileStep` and `RunProcessStep` are currently
  supported), `ExpectedOutputRelativePath`, `TimeoutSeconds`.
- The plugin's `BuildGenerationSteps` is allowed to throw on
  validation failure ("Save name is required"); the panel
  surfaces the message without firing the request.

**New wire DTOs in `GSM.Contracts\NodeApiContract.vb`:**

- `GenerateMapRequest` — `InstallPath`, `Steps As List(Of InstallStep)`,
  `ExpectedOutputRelativePath`, `TimeoutSeconds`.
- `GenerateMapResponse` — `Success`, `OutputRelativePath`,
  `OutputSizeBytes`, `FailedStepIndex`, `ErrorMessage`,
  `Output` (captured stdout truncated to 16KB).

**New node endpoint** `POST /api/instances/{id}/generate-map` —
synchronous, blocks until the steps complete or `TimeoutSeconds`
elapses. Validates that every step is one of the supported types
(rejects `SteamCmdStep`, `DownloadFileStep`, etc. with 400). Runs
them sequentially via the existing install-runner step
mechanics, then verifies `ExpectedOutputRelativePath` exists on
disk before returning success. Manager-side wrapper
`INodeClient.GenerateMapAsync` uses a one-shot
`HttpClient(Timeout=InfiniteTimeSpan)` with a caller-supplied
`CancellationToken` since map generation can run for minutes on
large worlds.

**Why a separate endpoint** (not just the install runner): we
don't want a half-failed generation cluttering install
operation history; the supported step types are a strict
subset; the operation runs against an existing install so
doesn't need credential handling or install lifecycle states.

**New file `GSM.Manager\UI\FileGenerationPanel.vb`** — generic
shell. Renders the plugin's `GetGenerationSchema()` via
`SchemaFormBuilder`, calls `BuildGenerationSteps()` on Generate,
posts to the node, displays progress and completion state.
Replaces the earlier `MapGenerationPanel.vb` (which now contains
an empty namespace stub kept in tree for diff-clarity; safe to
delete on next pass).

**`ManagedFilesPanel` integration:** the previous
`HasMapGenerationProvider` boolean was replaced with
`ResolveFileGenerationInfo()` which returns a `FileGenInfo`
bundle (provider + button label + tab title + target dir ref).
The "Generate New..." button only appears when
`info.GetTargetDirectoryRef()` matches the panel's directory.
Clicking it opens a sibling `FileGenerationPanel` tab; the
user can monitor the generation while still browsing the file
list.

**Factorio plugin updates** — implements
`IFileGenerationProvider` with:

- 7 hardcoded presets in `BuiltinPresets()`: Default, Death
  World, Rail World, Ribbon World, Rich Resources, Lakes,
  Island. Each backed by a `*Json()` method returning the
  corresponding `map-gen-settings.json` blob as a VB string
  constant. Drift risk vs. Factorio's in-engine presets
  documented in source.
- Schema: `Preset` (Enum, populated from preset display names),
  `SaveName` (Text, required), `Seed` (Text, optional, uint32
  validated locally before request).
- `BuildGenerationSteps` writes the preset JSON to a per-
  generation `map-gen-settings-{timestamp}.json` (so concurrent
  generations on the same install can't stomp each other),
  then runs `factorio.exe --create saves/<name>.zip
  --map-gen-settings <path> [--map-gen-seed <seed>]`. Filename
  normalised: leading paths stripped, `.zip` extension
  auto-appended.

**Deferred to v2:**

- Schema-driven custom presets (every map-gen-settings parameter
  exposed as form fields under `GetGenerationSchema`). Hardcoded
  presets stay valuable as starting points; this augments.
- Map exchange string import (D5 v2 note in the plan).
- Factorio scenarios. Scenarios use different CLI semantics
  (`--start-server-load-scenario` at runtime, not `--create`),
  and the documented behaviour of arguments like
  `--map2scenario` is unclear. Considered and shelved during the
  preset round.

### Phase 4c-4 — Structured config file editor

Last user-visible piece. Lets a plugin expose a known config
file (Factorio's `server-settings.json` is the canonical case)
as a structured form rather than raw text. File-as-truth per D1:
Manager fetches fresh from the node on tab open, writes back
on Save, never caches in the DB.

**Originally specced as Phase 4c-3 "Server config editing" in
the plan.** Renumbered during implementation — file ops (4c-1)
and saves UI (4c-2) needed to be solid first since the editor
rides on top of both. The original "Phase 4c-3 = server config"
numbering survives in some commit messages from earlier in the
phase.

**New contracts in `GSM.Contracts\IGamePlugin.vb`:**

- `IInstanceFileEditorProvider` opt-in interface with three
  methods:
  - `GetInstanceFileEditors(config) As IReadOnlyList(Of InstanceFileEditor)`
    — plugin returns one entry per file it can edit. Cheap;
    invoked once when the InstancePanel builds its tabs.
  - `ReadFileToValues(editorKey, fileText) As Dictionary(Of String, String)`
    — plugin parses the on-disk content into a flat values
    dict the schema form can render. Empty/null `fileText` is
    handled by returning an empty dict; schema defaults take
    over for missing keys.
  - `WriteValuesToFile(editorKey, values, existingText) As String`
    — plugin builds the new file text from form values.
    `existingText` is the verbatim file content last read;
    plugin parses it, updates schema-managed keys, re-serialises.
    **Unknown top-level fields the user added by hand outside
    the schema MUST round-trip unchanged.**
- `InstanceFileEditor` data class: `Key` (plugin-defined stable
  id, used to dispatch in multi-editor plugins), `TabTitle`,
  `RelativePath` (relative to install root; may contain
  `{InstanceId}` for future multi-instance games),
  `Schema As IReadOnlyList(Of ConfigFieldDescriptor)`.

**New file `GSM.Manager\UI\InstanceFileEditorPanel.vb`** —
generic shell. Header label, path label, scrollable form host,
bottom strip with Save / Reload / status. Logic:

- **On open** (`LoadAsync`): downloads the file via
  `INodeClient.DownloadFileAsync`. `allowedRoots` and
  `allowedExtensions` are auto-derived from `RelativePath` —
  for files at the install root (e.g. `server-settings.json`)
  the root is the filename itself (the file endpoint's
  equality check matches just that one file); for files under
  a subdirectory (e.g. `config/world.json`) the root is the
  parent dir. 404 → treats as empty file, renders form with
  schema defaults, status reads "doesn't exist yet — schema
  defaults shown. Save will create the file."
- **On Save** (`SaveClicked`): runs `_schemaResult.ValueExtractor`,
  calls plugin's `WriteValuesToFile(values, _lastDownloadedText)`,
  uploads via `INodeClient.UploadFileAsync(overwrite:=True)`,
  caches new text as `_lastDownloadedText` so a follow-up Save
  without an intervening Reload still has the up-to-date
  "existing" content.
- **On Reload**: confirms via MessageBox.YesNo (Reload is
  destructive of in-progress edits), re-runs LoadAsync.
- 404 detected via `IsNotFound(NodeApiException)` checking
  `InnerException` is `HttpRequestException` with
  `StatusCode = NotFound`. Anything else is a real error.
- `_disposeCts` cancellation-token-source tripped on Dispose so
  in-flight async resumptions bail out before touching disposed
  controls.

**`InstancePanel.BuildEditorTabs`** in `UiPanels.vb` — mirrors
`BuildManagedFilesTabs`. Resolves the plugin's
`IInstanceFileEditorProvider`, builds a merged install+instance
`InstanceConfig` (case-insensitive dict, instance overlays
install — same merge logic as `BuildPreFlightValidationWarnings`)
so the plugin sees the same merged view it sees at start time.
`{InstanceId}` substitution applied. Inserts editor tabs at
`_tabs.TabPages.IndexOf(_chatTab)` BEFORE `BuildManagedFilesTabs`
runs; the managed-files pass then finds Chat shifted by N and
inserts after, giving final order:
`Overview | Configuration | [editor tabs] | [managed dirs] | Chat`.
`TryFindInstall` helper opens a fresh DB scope rather than
holding the caller's scope across tab construction.

**Factorio plugin updates** — implements
`IInstanceFileEditorProvider` with one editor for
`server-settings.json`. Schema is 18 flat fields ordered
identity → visibility → auth → gameplay → saves:

```
Identity:    Name, Description, Tags, MaxPlayers
Visibility:  VisibilityPublic, VisibilityLan
Auth:        Username, Token, GamePassword, RequireUserVerification
Gameplay:    AllowCommands (Enum: true/false/admins-only),
             AutoPause, OnlyAdminsCanPause, AfkAutokickInterval
Saves:       AutosaveInterval, AutosaveSlots,
             AutosaveOnlyOnServer, NonBlockingSaving
```

Descriptions carry `[Section]` prefixes since `SchemaFormBuilder`
doesn't support section headers yet; visual grouping is
communicated via the prefix and field ordering. Adding
section-break support to SchemaFormBuilder is a v2 follow-on.

**JSON handling — `JsonNode`, not `JsonDocument`.** The plugin
imports `System.Text.Json.Nodes`. `JsonDocument` is read-only;
`JsonNode` (specifically `JsonObject` and `JsonArray`) is
mutable and supports the unknown-fields-round-trip requirement.
`ReadFileToValues` parses the file text into a `JsonNode` tree
and pulls each schema field via small typed helpers
(`ReadString` / `ReadInt` / `ReadBool` / `ReadAllowCommands`).
`WriteValuesToFile` parses `existingText` into a `JsonObject`
(starts fresh if missing or malformed), then `Set*` helpers
overwrite only the schema-managed keys via `JsonValue.Create`.
Unknown top-level fields (`segment_size_*`, `max_upload_*`,
anything else) round-trip verbatim because the JsonObject's
other properties are untouched. Output via `ToJsonString` with
`WriteIndented = True` so the file stays human-readable.

**Three Factorio-specific flattenings:**

- `visibility:{public, lan}` nested object → two top-level form
  fields (`VisibilityPublic`, `VisibilityLan`). Reader pulls
  from the nested object; writer reconstructs it, preserving
  any other sub-fields if present (e.g. `steam` on older
  Factorio versions).
- `tags` array → comma-separated text field. Reader does
  `String.Join(", ", tagList)`; writer splits on `,`, trims, and
  builds a fresh `JsonArray`. No new `StringList` field type
  introduced — wasn't worth the contract addition for one use
  case.
- `allow_commands` may legitimately serialise as either a JSON
  string ("admins-only" — modern docs' canonical form) or a
  JSON boolean (`true`/`false` — older form). `ReadAllowCommands`
  tries string first then bool. `SetAllowCommands` writes
  `"admins-only"` as a string but writes `"true"`/`"false"`
  values as actual booleans — Factorio rejects the strings
  `"true"`/`"false"` as invalid for that field.

**Deferred to v2:**

- Section-header support in `SchemaFormBuilder`. New
  `ConfigFieldType.SectionHeader` value, ~30 lines of rendering.
  Would let Factorio drop the `[Section]` description prefixes.
- Per-instance editor scope via `{InstanceId}` token —
  contract supports it, no current plugin uses it.

### Phase 4c file map

| Layer | File | Role |
|---|---|---|
| Contracts | IGamePlugin.vb | `IManagedDirectoriesProvider` + `ManagedDirectory` + `DirPermissions` (4c-1); `IFileGenerationProvider` + `GenerationStepBundle` (4c-3); `IInstanceFileEditorProvider` + `InstanceFileEditor` (4c-4); `ConfigFieldType.ManagedFilePicker` + `ManagedDirectoryRef` property on `ConfigFieldDescriptor` (4c-2) |
| Contracts | NodeApiContract.vb | `FileEntry` DTO (4c-1); file-ops methods on `INodeClient` (4c-1); `GenerateMapRequest` / `GenerateMapResponse` + `GenerateMapAsync` on `INodeClient` (4c-3 — NAMING NOTE in source) |
| Node | Endpoints\FileEndpoints.vb (new) | `/api/instances/{id}/files` CRUD + rename + copy with path validation, root allowlist, extension allowlist, streamed body for upload (4c-1) |
| Node | Endpoints\InstanceEndpoints.vb | `/api/instances/{id}/generate-map` synchronous endpoint with output-existence verification (4c-3) |
| Node | MapGenerationRunner.vb (new) | Sequential `WriteFileStep` + `RunProcessStep` runner with stdout capture and per-step timeout enforcement (4c-3) |
| Manager Core | NodeHttpClient.vb | `INodeClient` file-ops wrappers (4c-1); `GenerateMapAsync` with InfiniteTimeSpan one-shot HttpClient (4c-3) |
| Manager UI | UiPanels.vb | `InstancePanel.BuildManagedFilesTabs` (4c-2); `InstancePanel.BuildEditorTabs` + `TryFindInstall` helper (4c-4); `BuildPreFlightValidationWarnings` for `OnStartInstance` (4c-2); `SchemaFormBuilder.Build` 3-arg overload + `PopulateManagedFilePickerAsync` named helper (4c-2) |
| Manager UI | ManagedFilesPanel.vb (new) | File-list ListView + Upload/Download/Delete/Rename/Copy buttons; `ResolveFileGenerationInfo` integration for the "Generate New..." button (4c-2/4c-3) |
| Manager UI | FileGenerationPanel.vb (new) | Generic schema-driven generation UI hosting `SchemaFormBuilder` (4c-3) |
| Manager UI | InstanceFileEditorPanel.vb (new) | Generic structured file editor: download → plugin parse → schema render → plugin serialise → upload (4c-4) |
| Plugins | FactorioPlugin.vb | Implements `IManagedDirectoriesProvider` (saves/, .zip allowlist, R/W/D); `SaveFile` field as `ManagedFilePicker` (4c-2); `ValidateConfig` warns on missing save selection (4c-2); implements `IFileGenerationProvider` with 7 presets + uint32 seed validation (4c-3); implements `IInstanceFileEditorProvider` for server-settings.json with 18 fields, JsonNode-based parse/serialise preserving unknown fields, allow_commands string-or-bool dual handling (4c-4) |

---

### Startup config render (`IStartupFileProvider`)

A third field→runtime bridge, alongside `BuildLaunchArguments`
(CustomFields → command line) and the user-triggered
`IFileGenerationProvider`. This one renders selected instance-config
values into the game's **own** config file just before launch,
preserving everything else in the file. It closes two gaps the other
bridges couldn't:

- **File-only games can't use the node port allocator.** The allocator
  picks free values for `IsPort` fields in `GetInstanceConfigSchema` and
  stores them in `CustomFields`, but those only reach a game through
  launch args. Windrose has none — its port lived only in
  `ServerDescription.json` (the file editor), invisible to the allocator
  (Windrose Decision D2).
- **Arg-passed text garbles.** Conan's `ServerName` mangles on
  spaces/unicode through the launch URL, and a URL `?ServerPassword=`
  dies with `AESDecryptionFailed`; both read clean when the engine takes
  them from its config file.

**Contract (`GSM.Contracts\IGamePlugin.vb`)** — opt-in side-interface,
same pattern as `IInstanceFileEditorProvider` (VB can't add default
members to `IGamePlugin` without breaking every existing plugin).
`ContractsVersion` stays 2 — the still-in-dev v2 surface was never
released, so the render folds in without a bump; adopting plugins
declare `requiresContracts="2"`.

- `GetStartupFiles(instanceConfig) As IReadOnlyList(Of String)` —
  install-relative paths the plugin wants (re)written at start. Cheap;
  called every start.
- `RenderStartupFile(relativePath, instanceConfig, existingText) As String`
  — given the file's current on-disk text (`""` if absent), returns the
  new content with instance-config values injected. Return `Nothing` (or
  the unchanged text) to skip the write — and specifically return
  `Nothing` when `existingText` is empty if the game must generate the
  file itself first. Both consumers do this, so a brand-new instance's
  first launch lets the server create the file; values apply from the
  second start.

**Manager hook — `InstanceManager.ApplyStartupFileRendersAsync`**,
called from `StartInstanceAsync` after the three-layer config merge
(`MergeConfigLayers` → `CustomFields`) and before the
`StartInstanceRequest` is built. For each path the plugin lists: GET the
current text via the file editor's `DownloadFileAsync` (404 → `""`),
call `RenderStartupFile`, and PUT via `UploadFileAsync(overwrite:=True)`
only when the rendered text differs. It reuses the editor's node
endpoints and its allowed-roots / allowed-extensions derivation
(filename for a root file, parent dir for a subdir file; extension from
`Path.GetExtension`). Idempotent (render every start, write only on a
diff) and **proceed-and-warn** (O1): a per-file GET/PUT failure logs a
warning and the launch continues with the file's last values — a render
hiccup never blocks a start.

**Single-ownership rule (the catch).** A value rendered at start MUST
have one editable home, or the file editor and the Configuration tab
fight: the render runs last and would silently revert an editor edit. So
a field that becomes Configuration-owned (lives in `CustomFields`) is
**removed from the file-editor schema** for the same file. Per game,
networking/ports move to the Configuration tab; descriptive/world fields
stay in the file editor.

**Consumers:**

- **Windrose** (resolves D2, verified live) — `UseDirectConnection` +
  `DirectConnectionServerPort` (`IsPort`) moved to
  `GetInstanceConfigSchema` (so the allocator assigns/validates the
  port) and removed from the `ServerDescription.json` editor schema.
  `RenderStartupFile` writes both into the file's
  `ServerDescription_Persistent` object, skipping when the file is
  absent and stamping the port only when direct mode is on. Confirmed on
  a live UE5.6.1 run: a freshly-allocated port (50104) rendered into the
  file and bound by the engine in direct mode, with `CommandLine =
  ' -log'` carrying no config — allocator → Configuration → render →
  file → server, end to end.
- **Conan** — `ServerName` (off the launch URL) and `ServerPassword`
  (off the old Engine.ini editor tab) are now Configuration-tab fields
  rendered into `Engine.ini`'s `[OnlineSubsystem]`. The structured
  "Network (Engine.ini)" editor tab is removed (raw `Engine.ini` stays
  editable via the `.ini` file browser); the INI section-writer core was
  extracted from `WriteValuesToFile` into a shared
  `WriteIniSection(targetSection, schema, values, existingText)` the
  render reuses. `ServerName` always writes (blank → default name).
  `ServerPassword` is **set / keep / clear**: a non-empty field writes
  it; a blank field with the new `ClearServerPassword` checkbox unticked
  **preserves** the file's existing value (so an upgrade from the
  editor-tab version doesn't wipe a set password — the render simply
  omits the key from its schema, and `WriteIniSection` only writes keys
  the schema contains); a blank field with the checkbox ticked writes an
  empty password (open server). Render skips when `Engine.ini` is absent
  so the server creates it first.

Design rationale and decisions (D3, O1–O3) live in
`StartupConfigRender_Plan.md`.

---

### Phase 5m — Manager resilience (tray, safe mode, orphan guard)

**Safe mode.** `--safe-mode` (or an auto-offer when the previous run
left a crash marker) boots the Manager with the risky surfaces gated
off — `PluginRegistry.ReloadAll`, `AutomationEngine`, notifications +
Discord, `VersionCheckService`, `ChatRetentionPruner`, and node
background polling — while DB migrations, identity hydration, node
clients, and basic instance ops still run. The crash marker is a file
in `AppContext.BaseDirectory` written at startup and deleted on clean
shutdown; its presence next launch (with `--safe-mode` not explicitly
passed) triggers the recovery offer.

**Restart-into-mode must relaunch AFTER the outgoing process clears its
marker.** The File/tray "Restart in Safe Mode / Restart Normally"
entries set a relaunch request and close; the actual `Process.Start`
happens in `ManagerProgram.Main` *after* `Application.Run` returns and
the marker is deleted. Relaunching while the old instance is still
alive races the shared marker file two ways: the new instance reads the
not-yet-deleted marker and wrongly offers crash recovery, and the old
instance's clean-exit delete then removes the *new* instance's marker.
Sequencing the relaunch after clean shutdown avoids both.

**Orphan detection must be reconciliation-based, not diff-based.** The
hot-reload `DetectOrphans` diffs the previous loaded-GameId set against
the current one, so it only fires for a plugin removed *during a
running session*. On a fresh start `previousGameIds` is empty (the
registry just constructed), so a plugin deleted *between* sessions
produces zero warning. To catch startup / cross-session orphans,
reconcile directly: enumerate the GameIds referenced by
installations/instances in the DB and flag any with no loaded plugin
(`PluginOrphanDetector.BuildOrphanReport`). Run it at startup and after
every manual reload.

**A persisted `ExeOverride` will start an instance with no plugin —
guard `StartInstanceAsync`.** Every plugin call in the start path is
`If plugin IsNot Nothing` guarded, so a missing plugin yields empty
launch args and no parse rules — but `ExeOverride` (saved from a prior
successful, plugin-loaded start) is still a valid executable candidate,
so the node launches the bare binary: an unmanageable, untracked,
crash-looping process whose activity also goes unrecorded (the Manager
can't parse it). The fix is a hard guard at the top of
`InstanceManager.StartInstanceAsync` — refuse when
`_pluginRegistry.GetPlugin(gameId) Is Nothing`. It's the single
chokepoint every start path funnels through (panel, tree menu,
autostart, `RestartInstanceAsync`, scheduled restart), so disabling the
UI Start/Restart buttons is just the affordance, not the safety
mechanism.

**Banner dock order.** The `MainForm` notification banner (SAFE MODE /
orphan alerts) is a `Panel` docked `Top`, added to `Me.Controls`
*between* the status strip and the menu strip so WinForms docks it
directly beneath the menu and above the split (last-added `Top` docks
outermost).

**Plugin enable/disable uses a `Plugins\Disabled\` subfolder, not an
extension rename.** `ReloadAll` scans `Directory.GetFiles(dir, "*.vb",
TopDirectoryOnly)` — a subfolder is skipped unconditionally, whereas a
`.vb`→`.disabled` rename would lean on the Windows `*.vb` glob not
matching `name.vb.disabled`, which the legacy short-extension quirk
makes unsafe to assume. Moving the file keeps its name + `.vb`
extension (editor highlighting intact). A disabled plugin isn't loaded,
and `PluginRegistry` exposes no public GameId→source-file map, so the
enable/disable UI is file-centric (lists `Plugins\` + `Plugins\Disabled\`
directly) rather than operating on loaded-plugin GameIds.

**Safe-mode subsystems are re-enabled via a controller that mirrors
Main's per-subsystem start.** `ManagerProgram.StartSubsystem` holds the
on-demand start for each gated subsystem (idempotent, tracked in a
started-set); Main's normal-mode startup keeps its own inline blocks
(they carry extras like the plugin-summary status line), so the
controller's set stays empty on a normal launch — fine, since the panel
is safe-mode-only. VersionCheck must start the AutomationEngine first
(it raises version-mismatch events into the engine), so `StartSubsystem`
pulls it up as a dependency.

**`PluginStatusForm` reloads don't route through `MainForm`.** The form
calls `PluginRegistry.ReloadAll` directly, so an enable/disable+reload
there won't refresh the orphan banner/badges on its own — `MainForm`
re-runs `RefreshOrphanWarning()` when the dialog closes
(`OnPluginStatus`) to cover it.

**Watchdog and Manager are decoupled — process + exit codes + a shared
mutex *name*, no assembly reference.** `GSM.Watchdog` is a sibling
supervisor, not a library: the Manager doesn't link it, and the shared
single-instance mutex name lives as a hardcoded constant in *both*
projects (keep them in sync). They communicate via an exit-code
contract the Manager honours only when launched by the watchdog
(`POWERGSM_WATCHDOG=1` in its environment): `0` clean quit (watchdog
stands down), `10` deferred (a Manager was already running; this one
bowed out — not a crash), `20`/`21` relaunch normal / safe, any other
non-zero = crash. When watched, the Manager's in-app Restart sets
`Environment.ExitCode` to 20/21 and lets the process exit instead of
self-spawning, so the watchdog owns the relaunch and the replacement
stays supervised.

**Single-instance + self-relaunch: release the mutex before
self-spawning.** The Manager holds a named single-instance mutex for
its lifetime. In the *unwatched* Restart path it self-spawns the
replacement with `Process.Start` from the tail of `Main`, but the
process hasn't exited yet (still holds the mutex), so the new instance
would see the mutex held, treat itself as a duplicate, and bow out —
leaving no Manager. Fix: `ReleaseMutex()` + `Dispose()` the mutex
*before* the self-spawn. The watched path has no such race — the
watchdog relaunches only after the old process has fully exited (mutex
gone).

**Watchdog give-up exits 0, deliberately.** The rapid-restart give-up
(too many crashes in the window) returns `0`, not a failure code,
because the logon task carries a Task Scheduler `RestartOnFailure`
backstop: a non-zero give-up would trip the backstop and relaunch the
watchdog straight back into the loop it just abandoned. Exit 0 = the
watchdog deliberately stood down (it logs why); a *genuine* watchdog
crash (unhandled exception → non-zero) still trips the backstop, which
is the only thing it should catch.

**Console visibility is the exe subsystem, not the install method — the
watchdog is `WinExe`.** A console-subsystem `Exe` gets a console window
whenever launched interactively, including by an interactive logon
task, so the task can't hide it. The fix is `OutputType=WinExe` (same
as `GSM.Node`): no console ever; the watchdog logs to `watchdog.log`.
Entry point stays `Module Program` / `Sub Main`; stray `Console.Write*`
calls simply go nowhere under WinExe.

**Task Scheduler XML is order-sensitive and wants UTF-16.** The logon
task is created with `schtasks /Create /XML` (not inline `/TR` — the
install path has spaces, and only XML can set the restart backstop).
Two traps: (1) the child elements inside `<Settings>` must follow the
schema sequence exactly or `schtasks` rejects an "unexpected node"
(e.g. `DisallowStartOnRemoteAppSession` out of place); keep the order
matching a real Windows task export and omit version-fragile optional
nodes unless placed precisely. (2) Write the file as UTF-16
(`Encoding.Unicode`) to match the `encoding="UTF-16"` declaration. The
task runs `LeastPrivilege` + `InteractiveToken` as the current user, so
creating it needs no elevation (no UAC) and the Manager shows its
window with unchanged per-user/DPAPI scope. VB XML literals build the
definition and auto-escape the embedded path/user values.

**Watchdog co-location mirrors Node → CtrlCSender.** `GSM.Manager` has
a build-order-only `ProjectReference` to `GSM.Watchdog`
(`ReferenceOutputAssembly=false`, `SkipGetTargetFrameworkProperties`
for the net8.0-windows ↔ net8.0 mismatch) plus targets: `CopyWatchdog
ToOutput` (AfterTargets=Build) drops the framework-dependent watchdog
next to `GSM.Manager.exe` for dev, and `PublishWatchdog` /
`CopyWatchdogToPublish` publish it self-contained single-file into the
Manager's publish dir. So a normal Build co-locates the watchdog with
no manual copying, and `WatchdogTaskInstaller` points the task at
`AppContext.BaseDirectory\GSM.Watchdog.exe`.

---

### Self-update — detection & notification (Phase 5l-1)

First of three self-update sub-phases. **5l-1 only detects and
notifies** — it never downloads or applies anything (that's 5l-2 /
5l-3). `GitHubReleaseChecker` (Core, DI singleton) polls the Releases
API on a background loop (15s startup delay, interval from settings,
restart-tolerant throttle mirroring `VersionCheckService`),
`CheckNowAsync` bypasses the throttle for the manual check, and
`GetPersistedStatus` recomputes availability from stored values
without a network call. The running version comes from
`AssemblyInformationalVersion` (so the local-test trick is to lower
`<Version>` in `Directory.Build.props`). The checker never throws to
the UI — errors fold into `UpdateStatus.ErrorMessage`.

**Version comparison is custom, not `System.Version`.** Tags carry
`-rc` pre-release suffixes that `System.Version` can't hold;
`SemanticVersion` parses `vX.Y.Z[-pre][+meta]` and implements semver
precedence (release > matching pre-release; `rc1 < rc2`; numeric <
alphanumeric). Pre-releases are filtered out unless the user opts in.

**State lives in the existing settings key-value bag, not a new
table.** `update.*` keys via the `GsmDataExtensions` Get/Set helpers
(last-check, latest version/tag/body/url, skipped version, include-
prereleases, interval). This deliberately diverges from the original
plan's `UpdateCheckState` table — a KV bag needs no migration and
fits the read-pattern. An update *history* table is deferred to 5l-3
(where apply outcomes actually need recording).

**UI surface.** A status-bar indicator (hidden until a newer,
non-skipped release exists) opens a passive `UpdateDialog`; **Help →
Check for updates...** forces a poll then shows the dialog regardless;
**Skip this version** persists `update.skippedVersion` and re-hides the
indicator (a later, higher version un-hides it). `StatusChanged` fires
on the checker's background thread, so `MainForm` marshals via
`BeginInvoke` before touching the strip. The dialog only ever offers
**View on GitHub** (opens the browser) until 5l-2/5l-3 add a real
Download/Apply button.

**Install-writeability probe.** `InstallEnvironment.IsInstallWritable`
does a temp-file create-then-delete in `AppContext.BaseDirectory` —
a cheap proxy for "could self-update swap the binaries here". Run at
startup (one-time dismissable warning + a persistent "⚠ read-only
install" status-bar label) and re-run when the dialog opens (passed in
so the dialog surfaces a prominent warning instead of implying an
auto-update will work). Catches the Program-Files-without-elevation /
read-only-share / Controlled-Folder-Access cases.

**Release-notes rendering: HtmlRenderer + in-house Markdown→HTML, with
a fallback chain.** Notes render GitHub-style in an
`HtmlRenderer.WinForms` `HtmlPanel` (NuGet `HtmlRenderer.WinForms`
1.5.2 / `HtmlRenderer.Core` — pure-managed, net8-native since the repo
was modernised; no WebBrowser/ActiveX/WebView2 runtime). `MarkdownToHtml`
(UI) is our own small converter (headings, nested ul/ol, fenced +
inline code, bold/italic, links, blockquotes, rules; everything
HTML-escaped before markup so notes can't inject tags). Styled by a
GitHub-ish CSS string (`UpdateDialog.NotesStylesheet`). If HtmlRenderer
fails to load or throws, the dialog falls back to `MarkdownRenderer`
(a dependency-free Markdown→RichTextBox renderer) and finally to plain
text — a notes-render problem can never break the dialog. Gotchas
baked in here:

- **Render after the host's `Load`, not during control construction.**
  `RichTextBox` selection-formatting and `HtmlPanel` layout both need
  a created window handle; build the notes control in `OnLoad`.
- **A read-only multiline `TextBox`/`RichTextBox` selects all its text
  when it first gains focus.** Set `TabStop = False` (so the form
  focuses a button instead) + `HideSelection = True`.
- **Inline spans can wrap across physical lines.** The CHANGELOG hard-
  wraps bullets, so a `**bold**` span often opens on the marker line
  and closes on the next line. `MarkdownToHtml`'s list parser gathers
  each item's *lazy continuation lines* (non-blank, non-item,
  non-block via `IsBlockStart`) and joins them before inline parsing;
  paragraphs already join. Without this the stray opening `**` is
  consumed as an empty `<em></em>` and the text renders unbolded.
- **HtmlRenderer is CSS 2.1-ish** — no `border-radius` (code boxes are
  square-cornered), no flex/grid. Fine for notes. `font-family` with a
  space is finicky, so the body font is set via the control's `Font`
  and only single-word `Consolas` is named in CSS. Links are opened in
  the real browser via the `LinkClicked` event with `e.Handled = True`.
- **`sub` is a reserved word** — the dialog's sub-line label is
  `subLabel`, not `sub`.

---

### Self-update — download & stage (Phase 5l-2)

Second self-update sub-phase. `UpdateOrchestrator` (Core, DI
singleton) turns "an update is available" into "a verified copy sits
ready on disk" — without touching the running install. `StageAsync`
fetches the release by tag (`/releases/tags/{tag}` — the checker's
DTO omits assets, so this service has its own minimal asset DTOs),
downloads `SHA256SUMS` + the Manager zip, verifies, and extracts to
`<install>\.updates\{version}\extracted\`. `GetStagedState` confirms
the extracted folder still holds `GSM.Manager.exe`; `DiscardStaged`
wipes it. The staged version is tracked in the `update.stagedVersion`
setting key. `UpdateProgressDialog` drives a stage with a cancellable
progress bar; the update dialog grows a **Download update** button and
an **Update ready** state (Apply disabled pending 5l-3, plus Discard).

Applying the staged update (binary swap + rollback) is **Phase 5l-3**,
not built yet. Things worth remembering here:

- **Asset name strips the leading `v`.** `UpdateStatus.LatestVersion`
  comes from `SemanticVersion.ToString()`, which returns the raw tag
  (`v0.3.0`). The pipeline names the zip from the tag minus the `v`
  (`PowerGSM-Manager-0.3.0-win-x64.zip`, via
  `${GITHUB_REF#refs/tags/v}`), so the orchestrator strips a leading
  `v` when building the asset name. The staging folder + DB key keep
  the raw form, which is internally consistent (GetStagedState
  compares against `LatestVersion`).
- **Infinite HttpClient timeout + cancellation, not a wall clock.** A
  137 MB zip would blow a default 100s timeout; the client uses
  `Timeout.InfiniteTimeSpan` and relies on the dialog's Cancel button
  (the `CancellationToken`) to abort. The download loop is
  `ResponseHeadersRead` + a `Stream` copy reporting bytes; verify and
  extract report as indeterminate phases.
- **Never throws to the UI.** Success / cancel / error all come back
  in `StageResult`; cancel and failure both delete the partial
  version folder. The progress dialog refuses to close mid-stage
  except via its own completion (Esc / Alt-F4 route to a cancel).
- **Checksum is best-effort against old releases.** Releases cut
  before the SHA256SUMS pipeline step have no sums asset; the
  orchestrator logs a warning and stages without verification rather
  than failing. New releases verify.
- **`Progress(Of T)` marshals for free.** It's constructed on the UI
  thread in the dialog, so the orchestrator can `Report` from its
  background download loop and the callback lands on the UI thread.
- **`StageProgress` is a `Structure` with auto-properties + object
  initializers** — fine in VB; reported by value.

---

### Self-update — apply (Phase 5l-3)

The binary swap. A running `.exe` can't overwrite itself, so the
Manager hands the swap to a generated batch script and gets out of
the way. `PluginCompatibilityChecker` (Core) dry-run-compiles every
plugin `.vb` against a chosen `GSM.Contracts.dll` — the *staged* one,
as the apply pre-flight — mirroring `PluginRegistry` exactly
(same `ReferenceAssemblies.Net80` refs, options, and `Emit`) so a
green verdict means the file would really load. `CompatReportDialog`
renders it; in apply mode an incompatibility is a soft warning gated
by an acknowledgement checkbox.

Apply flow: **Apply update** → downgrade guard → automation-in-flight
warning → running-instances warning → staged-contracts compat report
→ `UpdateOrchestrator.RequestApply` generates `apply.cmd` → the dialog
closes the Manager → `ManagerProgram` spawns the script on exit and
quits clean. Things worth remembering:

- **Two informed-consent pre-flight warnings precede the compat
  report** (so the report's "Apply update" stays the final click):
  one if an automation rule is mid-execution
  (`AutomationEngine.GetRunningRuleNames` — names it, warns it won't
  resume), one if instances are running
  (`InstanceManager.GetRunningInstanceCount` — reassures that the game
  servers run on the *node* and keep running; only the Manager's log
  streams blink, and the restarted Manager reconnects and catches up
  on everything that happened while it was down — joins, leaves,
  server state, chat). Both are Yes/No; No just cancels the apply.
  Nothing ever stops a game server.
- **The compat checker is staged-contracts-only.** Its standalone
  **Tools** entry was dropped as redundant with **Plugin Status**
  (loading a plugin *is* compiling it against the current contracts,
  which Plugin Status already reports). The checker +
  `CompatReportDialog` stay; their unique value — compiling against
  the *future* contracts — is the apply pre-flight.

- **Exit code 0 is load-bearing.** The on-exit spawn path `Return`s
  from `Main` *without* setting a relaunch code, so the process exits
  0. The 5m-3 watchdog treats 0 as "clean quit — stand down," so it
  does NOT relaunch the old binary mid-swap. `apply.cmd` owns the
  relaunch. The replacement runs unwatched until the next logon
  restarts the watchdog (which then monitors via the mutex) — an
  accepted degradation, not a bug.
- **`apply.cmd` waits on `tasklist`, not a timer.** It loops until
  `GSM.Manager.exe` is gone from `tasklist` before copying (the
  running `.exe`/`.dll` are locked; this also rides out an AV pause),
  then backs the current two binaries up to `.updates\rollback\`,
  copies the staged `GSM.Manager.exe` + `GSM.Contracts.dll` over the
  install, `start`s the new one with `--post-update {version}`, and
  `del`s itself. A copy failure jumps to `:fail`, which appends to
  `.updates\apply-error.log` and exits 1 (no relaunch).
- **Only the two binaries are ever touched** — never `gsm.db`,
  settings, `Plugins\`, `Logs\`, or the (possibly-running, possibly-
  locked) watchdog. The runtime DB path is anchored to
  `AppContext.BaseDirectory`, so the relaunch's working directory
  can't point it at the wrong `gsm.db`.
- **Script is written without a BOM** (`UTF8Encoding(False)`): a
  UTF-8 BOM ahead of `@echo off` breaks cmd parsing. And the spawn is
  `cmd /c ""<path>""` (double-quote wrapped) so a path with spaces
  survives cmd's `/c` quote-stripping; `Chr(34)` builds the quotes to
  keep the VB readable.
- **Downgrade guard.** `StagedVersusRunning` compares via the
  checker's `SemanticVersion`; apply refuses a staged version older
  than the running one (belt-and-suspenders — the checker never
  *offers* a downgrade, but a stale staged folder could harbour one).
  It can't catch an artificially-lowered local `<Version>` test,
  which is why a forward `-rc` tag is the honest apply test.
- **`--post-update {version}`** is parsed early in `Main`; after the
  DB is ready, `CompletePostUpdate` deletes the staging version
  folder (keeps `rollback\`) and clears the staged + latest keys so
  the next poll re-detects fresh. `TakeApplyError` is checked on
  *every* startup so a failed swap that never relaunched still
  surfaces. Rollback is manual for now (copy back from
  `.updates\rollback\`); the semi-automatic path is a documented
  non-goal for v1.
- **Every apply attempt is recorded to update history.**
  `UpdateHistoryEntity` (table `UpdateHistory`, migration
  `UpdateHistory`) gets a row from `CompletePostUpdate` on a
  successful post-update startup and from `RecordFailedApply` when
  `TakeApplyError` finds a log. The *from* version is stashed in the
  `update.pendingFromVersion` key by `RequestApply` (the outgoing
  build) so the post-update build can record from→to across the swap.
  `GetHistory` feeds the read-only `UpdateHistoryDialog` under
  **Help → Update History**. Both halves need the recording code, so
  an apply from a build that lacks it records a blank/absent *from*
  (0.3.0 has neither half). `AppliedAtUtc` reads back from SQLite as
  `Kind=Unspecified`, so the dialog `SpecifyKind(Utc)` → `ToLocalTime`
  before display.
- **Dev bypass.** Building from source overwrites the binary anyway,
  so to undo an apply during testing just rebuild (or copy the two
  files back from `.updates\rollback\`).

**Build gotcha — `PublishWatchdog` TFM leak (5m-3 infra, fixed here).**
A `Publish` sets `TargetFramework=net8.0-windows` as a *global*
property for the Manager's inner build, and the `<MSBuild>` task
passes inherited globals to the child project. The Manager's
`PublishWatchdog` target therefore asked the `net8.0`-only watchdog
to publish as `net8.0-windows` — failing with "Assets file … doesn't
have a target for net8.0-windows". Fix: `RemoveProperties="TargetFramework"`
on that `<MSBuild>` call. The regular `<ProjectReference>` to the
watchdog avoids this via `SkipGetTargetFrameworkProperties=true`; the
explicit `<MSBuild>` invocation needed its own guard. A plain Build
doesn't set `TargetFramework` globally, so only Publish tripped it —
and since the watchdog bundling postdates v0.3.0, it would have hit
the next tagged release too.

---

### Notification routing & scope

How an in-Manager event becomes a Discord message, and which
destinations it reaches. The transports predate Phase 5n; the
four-dimension scope model and the fan-out are 5n.

**Pipeline.** `NotificationEmitter` (Core, DI singleton) is the single
entry point — typed methods (`InstanceStarted/Stopped/Crashed`,
`CrashLoopDetected`, `UpdateStarted/Completed/Failed`,
`PlayerJoined/Left`) each call a private `FireAsync(eventType,
instanceId, installationId, message, tokenCustomizer)`. `FireAsync`
builds a `NotificationContext` (below) and fans it to the notification
transports (it also raises `Emitted`, which utility plugins tap —
Phase 7-2). There is **no shared scope router**: the webhook and bot
transports are independent consumers, each with its own destination
cache and its own copy of the matcher (see *Two transports*).

**`BuildContextAsync` — the context.** Opens a scoped `GsmDbContext`
and loads either the instance (`Include(Installation).ThenInclude(Node)`)
or, for installation-level events, the installation (`Include(Node)`),
then stamps a `NotificationTokens` bag: `NodeId/NodeName`,
`InstallationId/InstallationName`, `GameId`, `BuildId` (parsed from
`InstalledVersion`), `InstanceId/InstanceName`, `InstanceSetTag`, and a
best-effort `TileId/TileName` (one `/server-state` round trip to the
node; failures silent). Tokens feed both template substitution and the
scope matcher.

**Event levels.** Two shapes, distinguished by which id `FireAsync`
gets:
- **Instance-level** (start / stop / crash, crash-loop, player
  join / leave) — carry `instanceId`; installation and node are
  reached *through* the instance.
- **Installation-level** (the three Update events) — carry only
  `installationId`, `instanceId = Nothing`. There is no single
  instance, so no instance or set token of their own. (Updates fire
  even when nothing changed: `UpdateStarted` is unconditional and
  `UpdateCompleted` fires on success, which includes SteamCMD's
  "already up to date".)

**Scope: four-dimension union-of-includes (5n).** A destination filters
on any of Node / Installation / Instance / Instance-set. The rule is
"an event is in scope if it matches **any** populated dimension; the
all-empty destination matches everything." This replaced a two-filter
AND-narrowing whose "empty = all within" rule was invisible. Set tags
reuse the per-instance `InstanceEntity.InstanceSetTag` — no new entity;
the notifications form only *consumes* the tag (it's authored in
`EditInstanceForm`).

**Scope fan-out (the non-obvious part).** Because installation-level
events have no instance, a naive matcher could never match them against
an instance- or set-scoped destination — counter-intuitive, since "my
production set" plainly *should* hear about an update to an
installation that hosts that set. So at emit time the context carries
**every** scope identifier the event relates to, and the matcher
intersects. `NotificationContext.ScopeInstanceIds` /
`ScopeInstanceSetTags` (matching-only collections, *not* template
tokens) hold the single instance + tag for instance-level events, and
**all** instances under the installation + their distinct non-empty
tags for installation-level events — populated by a direct
`db.Instances.Where(InstallationId=…)` query in the emitter, so there's
no cache and no staleness. The matcher tests node/installation against
the single tokens (`Hit`) and instance/set against the collections
(`HitAny`). Model: "an event carries every scope identifier it relates
to; a filter matches on intersection."

**Comparers.** The ID filters (node / installation / instance — GUIDs)
are `OrdinalIgnoreCase`; the set filter is `Ordinal`, matching
`RuleScope.InstanceSet`'s query-time comparison. They must not share a
comparer.

**Two transports, duplicate matchers.** `DiscordWebhookPlugin` and
`DiscordBotPlugin` each keep their own per-destination cache
(`DestinationCacheEntry` / `BotDestinationCacheEntry`, rebuilt from the
DB) holding the four parsed filter `HashSet`s, and their own copy of
the union `MatchesEvent` + `Hit` / `HitAny` helpers (filters parsed via
`ParseStringSet` / `ParseDestStringSet`). A change to scope semantics
must touch **both** or the two channels drift. The event-type gate
stays a separate AND in front of the scope gate. *(The bot also has an
unrelated `MatchesPanelScope` for its live control panels — a different
feature, left untouched by 5n.)*

**Template token.** `{InstanceSetTag}` is substitutable in custom
templates, wired in `DestinationQueue`'s hardcoded `ReplaceToken` map
(substitution is not reflection, so it was added explicitly after
`{InstanceId}` — it did *not* "fall out for free"). It is
**single-valued and reflects `tokens.InstanceSetTag`**, so it renders
empty on installation-level update events (no single instance to name);
fan-out is a matching concern only. No default embed field was added,
to keep default notification appearance unchanged.

**Schema + editor.** `NotificationDestinationEntity` carries
`NodeFilterJson` / `InstallationFilterJson` / `InstanceFilterJson` /
`InstanceSetFilterJson` (migration `NotificationScopeDimensions`). The
editor is a four-section collapsible accordion in `NotificationsForm`
with live per-section summaries and a "matches N of M instances"
readout; the WinForms layout specifics (one growing, panel-scrolled
column; grow-to-fit lists; wheel forwarding) live in `Phase5n_Plan.md`
→ *As-built notes*.

**Back-compat.** Pre-5n rows that set *both* installation and instance
filters meant AND ("those instances *within* those installations");
under union they broaden to "those installations ∪ those instances."
Decision: no migration normalisation — such rows are left byte-for-byte
and reconfigured once. Rows with at most one filter set, and all-empty
rows, behave identically under both models.

**Files.** `GSM.Manager\Core\NotificationEmitter.vb` (emit + context +
fan-out); `GSM.Manager\Core\DiscordWebhookPlugin.vb` +
`GSM.Manager\Core\DiscordBotPlugin.vb` (caches + matchers);
`GSM.Manager\Core\DestinationQueue.vb` (template substitution + embed
building); `GSM.Manager\UI\NotificationsForm.vb` (editor);
`GSM.Contracts\INotificationPlugin.vb` (`NotificationContext`,
`NotificationTokens`); `GSM.Manager\Data\GsmDbContext.vb`
(`NotificationDestinationEntity`).

---

### Node updates — Manager → node binary push (Phase 8-2 slice 7a)

Distinct from the Manager's *own* self-update (the Phase 5l sections
above, Help → Check for updates): this is the Manager **pushing** a node
binary down to a configured node and triggering its update-exit. Slice 6
built the node's receive / apply / survive side; 7a is the Manager driver
+ the operator UI. Manager-only — nothing here touches `GSM.Contracts`.

**Push transport (`NodeHttpClient`).** Two methods, deliberately
**concrete-only on `NodeHttpClient` rather than on `INodeClient`** (the
`TryGetCachedVersion` precedent), so adding them caused no full-solution
Contracts rebuild. `NodeHttpClientFactory.GetClient` returns
`INodeClient`, so a caller drives the push by `TryCast`-ing to
`NodeHttpClient` (the same cast the node-icon resolver already uses for
`TryGetCachedVersion`).
- `StageBinaryAsync(target, localFile, version, ct)` — SHA-256 + sizes
  the file, then walks the node's chunked staging endpoint: `begin`
  (declares target / total / sha / version → `uploadId`) → `chunk*`
  (8 MB, raw octet-stream to `…/{uploadId}/chunk?offset=N`, append-only;
  a 409 carries the node's `expectedOffset` and we re-seek + resume) →
  `commit` (the node re-verifies size + SHA-256 over the whole file
  before atomic-renaming `.part` → the target's `.new`). The whole
  sequence runs on a **one-shot `HttpClient` with
  `Timeout.InfiniteTimeSpan`** (copying BaseAddress + Authorization off
  the shared client), same rationale as `UploadFileAsync` — the shared
  30 s timeout would chop a tens-of-MB push.
- `ApplyUpdateAsync(target, ct)` — POSTs `apply-update?target=…`; 202
  returns the survivor the node chose, 409 → `NodeApiException(Conflict)`
  when nothing is staged. The node tears down right after replying, so
  the caller then polls `/api/version` to confirm relaunch.
- Responses parse via small `Friend` DTOs (`StageBeginResponse`,
  `StageCommitResponse`, `StageApplyResponse`) with lowercase props to
  match the node's anonymous-object JSON (`ReadFromJsonAsync` binds
  case-insensitively). The Manager can't reference `GSM.Node` DTOs (it
  references only `GSM.Contracts`), so request bodies are anonymous
  objects and the responses are these local types.

**Update Nodes UI (`NodeUpdatesForm`, Nodes → "Update Nodes…").**
Modelled on `PluginUpdatesForm` (the per-item "select + trigger" list
precedent). A checkbox `ListView` — Node / Address / Installed (build) /
Platform / Status / Result — one row per `db.Nodes` entry. On open (and
Re-check) every node is probed via `GetApiVersionAsync` concurrently,
each bounded to ~8 s by a `CancellationTokenSource` so one unreachable
node doesn't hold the list at the client's 30 s timeout; unreachable
rows are greyed and the `ItemCheck` handler vetoes checking them.
`Update…` runs each checked node in turn — Staging… → Applying… →
Relaunching… → `Updated → {build}` (or `Failed: …`) into that row's
Result; a per-node `Try/Catch` means one failure never aborts the batch.
The relaunch poll (`WaitForNodeBackAsync`) waits 2 s, then forces
`GetApiVersionAsync(force:=True)` every 2 s up to 60 s, swallowing the
connection errors that are expected while the node is down.

**Per-node independence + per-target separation (design).** Node /
shim / NodeSetup updates are deliberately decoupled from the Manager's
own self-update *and from each other* — a multi-node fleet always has
some node offline, mid-session, or one the operator doesn't want to
touch yet, so the surface is a fleet view with per-node selection, not a
coupled "push everything." A **Target** combo (Node / Shim / NodeSetup)
is present from day one with only **Node** wired; the other two snap back
to Node with a status note (the node side only implements `ResolveTarget`
`Case "node"` until 7b/7c).

**Manual push as a first-class path + OS-match guard.** The file picker
is a permanent capability, not 7a scaffolding: an operator may push a
release build *or* their own build and owns the versioning +
consequences (the node's commit-time SHA-256 + size check and the
survivor relaunch are the integrity backstops). The picked file's
**actual format is sniffed from its magic bytes** (`DetectBinaryPlatform`
— `0x7F 'E' 'L' 'F'` → Linux, `MZ` → Windows; OS-level only, not arch),
independent of filename, and must match each target node's reported
`NodePlatform`. A **mixed-platform selection pops one selector per
platform present** (`PickBinaryForPlatform` per distinct OS), then maps
each node to the binary it can run; a wrong-format pick offers
Retry/Cancel, an unrecognised file warns first, and a node whose OS was
never reported (`Unknown`) is skipped in a typed push (a pure-Unknown
selection falls back to a single wildcard file). The optional Version
field is prefilled from the file's `ProductVersion` when blank (metadata
for the node target; structural for the shim target later).

**Files.** `GSM.Manager\Core\NodeHttpClient.vb` (`StageBinaryAsync` /
`ApplyUpdateAsync` + `ComputeFileSha256Async` / `ReadExpectedOffsetAsync`
+ the response/result DTOs); `GSM.Manager\UI\NodeUpdatesForm.vb` (the
form); `GSM.Manager\UI\MainForm.vb` (Nodes → "Update Nodes…" menu +
`OnUpdateNodes`). Node side (slice 6): `GSM.Node\SelfUpdate.vb`,
`GSM.Node\Endpoints\NodeEndpoints.vb`.

---

### Node updates — feed sourcing + Latest column (Phase 8-2 slice 7-source)

Builds on 7a's manual push: rather than hand-pick a binary, the operator can
see what the newest release is and have the Manager source the per-platform
node binary straight from the GitHub release feed. Manager-only — nothing here
touches `GSM.Contracts`.

**7-source-a — Latest column.** `NodeUpdatesForm` resolves the newest release
once per load via the existing `GitHubReleaseChecker` (the same singleton the
Manager's own self-update uses): `GetPersistedStatus()` first (instant, the
background checker's last result), falling back to one bounded `CheckNowAsync`
only when nothing's cached yet. A new **Latest** column then compares each
reachable node's installed build against it with `SemanticVersion` — a node
behind the release reads `X (update)` and its whole row is tinted
(`UpdateAvailableColor`), a current node reads `current`, and an unparseable
build just shows the bare latest. The status line gains a `· latest release X`
note. Best-effort throughout: a feed miss degrades the column to `—`, never an
error. The resolver stashes both the stripped **version** (for the column) and
the raw **tag** (`_latestTag`, for sourcing) off the one `UpdateStatus`.

**7-source-b — one-click feed sourcing.** A **Latest release** checkbox
(disabled when no `NodeReleaseSource` is registered) swaps the per-platform file
picker for a download. `OnUpdate` then dispatches to `OnUpdateFromFeed` instead
of `OnUpdateManual`; both converge on a shared `RunStagingAsync(plan)` that runs
the identical stage → apply → `WaitForNodeBackAsync` loop, so the only thing the
feed path changes is *where the binary comes from*. Per distinct platform in the
checked selection it confirms (one `GSM.Node[.exe] vX → N node(s)` line each),
then sources one verified binary per platform and feeds the plan. Unknown-
platform nodes have no release asset to match and are skipped.

**`NodeReleaseSource` (`GSM.Manager.Core`, new).** `SourceAsync(platform, tag,
progress, ct)` → `NodeSourceResult`: maps the platform to a rid
(`win-x64` / `linux-x64`; `Unknown` → fail), reads the configured update source
(`GsmDataExtensions.SettingKeys.UpdateSource`, same as `UpdateOrchestrator`),
finds that release's `PowerGSM-Node-{ver}-{rid}.zip` + `SHA256SUMS`, downloads
the zip, **SHA-256 verifies it against the sums entry**, extracts it, and
locates the inner `GSM.Node[.exe]` flat at the zip root — returning that local
path, which feeds the same `StageBinaryAsync` the manual pick does. Key
properties:
- **One download per (version, rid), cached + serialized.** A `Dictionary`
  keyed `version|rid` under a `SemaphoreSlim(1,1)` gate means a batch of
  same-platform nodes downloads once (a 5-Linux-node update fetches a single
  zip); the cache re-checks `File.Exists` before reuse and the extract dir lives
  under `<install>\.node-updates\<ver>\<rid>\` (fresh-wiped per source, cleaned
  on failure/cancel).
- **Never throws on a sourcing failure** — the outcome (incl. cancellation) is
  on `NodeSourceResult`; the form marks the affected nodes `Failed: …` and
  proceeds with the rest.
- **Own infinite-timeout `HttpClient`** (PowerGSM UA + GitHub accept/api-version
  headers), same rationale as the push client.
- The trust chain is release `SHA256SUMS` → verified zip → extracted binary →
  the existing `StageBinaryAsync` push (which re-SHAs the binary on the wire) →
  the node's commit-time re-verify. The node zip also carries `GSM.Shim/` +
  `GSM.NodeSetup`, so the later shim (7b) / NodeSetup (7c) co-updates source from
  the same download.

**Shared release-asset helpers (`ReleaseAssets.vb`).** The asset-fetch /
find-URL / parse-sums / SHA-256 / download-with-progress helpers (and the
`ReleaseWithAssets` / `ReleaseAsset` DTOs) that drive the Manager's own
self-update were lifted out of `UpdateOrchestrator` into a shared
`ReleaseAssetHelpers` module (all statics taking an `HttpClient`), so node
sourcing and Manager self-update share one verified-download path instead of two
copies.

**Gotcha — `NodePlatform` is `GSM.Plugin`, not `GSM.Node.Api`.** `NodeReleaseSource`
and `NodeUpdatesForm` both need `Imports GSM.Plugin` for the enum (the form
imports both namespaces; a file with only `Imports GSM.Node.Api` gets BC30002).
And `selected.Count(predicate)` binds to the `List.Count` *property* in VB, not
the Linq overload — use `selected.Where(predicate).Count()`.

**Files.** `GSM.Manager\Core\NodeReleaseSource.vb` (new) +
`GSM.Manager\Core\ReleaseAssets.vb` (new, shared helpers) +
`GSM.Manager\Core\UpdateOrchestrator.vb` (delegates to the shared helpers) +
`GSM.Manager\UI\NodeUpdatesForm.vb` (Latest column + feed mode) +
`GSM.Manager\ManagerProgram.vb` (DI registration).

### Node updates — shim + NodeSetup co-update (Phase 8-2 slices 7b/7c)

The **Target** dropdown in *Nodes → Update Nodes* (Node / Shim / NodeSetup) now
drives the push end to end; all three source from the **same** node release zip
(manual pick or feed).

**`NodeUpdatesForm`.** A `_target` field (`node` / `shim` / `nodesetup`) is set
from the combo (`OnTargetChanged`). `RunStagingAsync` stages + applies `_target`:
**node** then polls `/api/version` for the relaunch (as in 7a), while **shim /
nodesetup** report `Installed` and skip the poll (the node never bounces). Shim
versions are stripped at `+` before staging so the node's `GSM.Shim\<version>\`
folder name parses (`Version.TryParse` rejects `+sha` build metadata). The manual
file picker and both confirm prompts are target-aware (binary base name via
`TargetBaseName()` + effect wording via `ApplyEffectNote()` — only Node warns of
a brief offline).

**`NodeReleaseSource.SourceAsync(platform, tag, target, progress, ct)`** gained
the `target` arg (cache key is now `version|rid|target`). Because node, shim, and
NodeSetup all ride one node zip, an already-extracted (version, rid) is reused —
no second download, no destructive re-extract — and `LocateTargetBinary` finds
the requested binary inside it: `GSM.Node[.exe]` / `GSM.NodeSetup[.exe]` at the
zip root, `GSM.Shim[.exe]` under its `GSM.Shim\<ver>\` folder. (Supersedes the
"flat at the zip root / keyed `version|rid`" description in the 7-source section
above.)

**Node side** does the actual work per target *shape* — see `reference/node.md`
(self-update): node = swap-with-survivor (bounces), nodesetup = in-place swap of
the idle binary (no bounce, no auto-revert), shim = versioned install at commit
(lock-safe, then a clean 409 if a running shim pins the file).

**Files.** `GSM.Manager\UI\NodeUpdatesForm.vb` (Target wired) +
`GSM.Manager\Core\NodeReleaseSource.vb` (`target` arg + `LocateTargetBinary`).
No `GSM.Contracts` change — `StageBinaryAsync` / `ApplyUpdateAsync` already took
an arbitrary target + version.
