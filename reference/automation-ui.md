# PowerGSM Reference — Automation (UI)

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
The forms half of the automation refactor: the EditInstanceForm restart-
schedule section, the InstallationPanel reorder UI, tree-state
preservation, state-driven instance buttons, the non-modal
AutomationRulesForm, and the RuleEditorForm rewrite (trigger / scope /
action editors, tabbed layout, sequence editor). The engine,
coordinator, serializer, and rule-materialization half is in
[`automation-core.md`](automation-core.md), which also holds the
cumulative automation file map.

---

### EditInstanceForm Restart Schedule section (in `RemainingForms.vb`)

Form size grew 580×560 → 580×755 to fit the section. Config panel
shrunk 300→220 height to partially compensate.

**Layout:** two sibling panels at the same coordinates (`520×240`):
- `_normalPanel` — standard editable controls (visible by default)
- `_driftPanel` — warning text + "Open in Automation Rules..." button

Visibility toggled as a unit via `ApplyDriftState()`. Toggling at
the panel level handles all sub-widgets (including unstored static
labels like "Cron:", "Hour:", "Stagger step:") without per-control
visibility tracking.

**Normal panel contents (top to bottom):**
- Enable scheduled restart checkbox (master toggle)
- Cron text field + live next-run preview ("Next: Fri 4:00 AM (in 18h 23m)" or "Invalid cron expression" in red)
- "Set Daily" preset: hour numeric (0–23) + button → writes `0 H * * *`
- "Set Interval" preset: hours numeric (1–24) + button → writes `0 */N * * *`
- Stagger step numeric (0–60, default 5; 0 = no stagger / literal copy)
- **Propagation:** mutually-exclusive radio group:
  - "This instance only" (default)
  - "Stagger across enabled siblings (renumber by SortOrder)"
  - "Apply same cron to enabled siblings (no stagger)"
- "Enable scheduled restart on all instances first" checkbox (one-way ON only)
- Help text explaining the queue model

**Helper:** `ApplyMinuteOffsetToCron(cron, offsetMinutes) As String`
(Friend Shared) — parses a cron, adds an offset to its minute field,
bumps the hour on overflow when hour is also numeric. Wildcard or
step-style hours (`*`, `*/12`) are left untouched. Negative offsets
supported via floor-divide trick:
`hourBump = (totalMinutes - newMinute) \ 60` (VB's `\` operator
truncates toward zero, so the standard-mod approach loses the borrow
on negatives — the floor-divide form is exact).

**Three states handled on load:**
- **No rule** (RuleId null or entity missing) — load from
  `Instance.RestartCron` cache; orphan case treated as fresh
- **Simple rule** — pull cron from the rule's `ScheduleTrigger`
  (authoritative), not from `Instance.RestartCron` (cache)
- **Drifted rule** — drift panel shown; restart fields on Instance
  NOT touched on save, preserving power-user edits

**Save path — six scenarios all handled:**

| Enable-all | Propagation | Result |
|---|---|---|
| ☐ | None | Just this instance |
| ☐ | Stagger | Stagger across currently-enabled siblings |
| ☐ | Apply same | Literal cron to currently-enabled siblings |
| ☑ | None | Enable everyone, no cron propagation |
| ☑ | Stagger | Enable everyone first, then stagger across all |
| ☑ | Apply same | Enable everyone first, then literal cron to all |

Order of operations matters: Enable-on-all runs FIRST so newly-
enabled siblings count as enabled in the propagation set.

**Stagger formula:** for the active set (this instance + enabled
non-drifted siblings, sorted by SortOrder), find this instance's
renumbered position, then for each sibling at active-position M:
```
newCron = ApplyMinuteOffsetToCron(thisCron, (M - thisPosition) * step)
```
Example: 5 instances all enabled, this is at SortOrder 3 with
`30 4 * * *` typed, step 5 → SortOrder 1=`20 4`, 2=`25 4`, 3=`30 4`,
4=`35 4`, 5=`40 4`. The user's typed cron stays on this instance
untouched; everyone else fans out from there.

**Drift-skip:** any sibling with a drifted rule (via
`IsSiblingDrifted` helper) is left alone in all paths. Drifted
siblings don't get assigned a position in the renumbered active
set and don't get cron writes.

**Engine reload:** `engine.ReloadRules()` is called only when at
least one rule actually changed (`anyRuleChanged` flag). No-op
saves don't trigger a reload, keeping log spam low.

### InstallationPanel reorder UI

In `UiPanels.vb`, `InstallationPanel` got Up/Down buttons in a
right-docked button column next to the instances list:

- New `#` column showing 1-based renumbered position (matches what
  the stagger algorithm computes internally)
- Listview now sorted by `SortOrder ASC`, then `CreatedUtc` as
  tiebreaker
- `OnReorderInstance(direction)` swaps `SortOrder` values with the
  adjacent sibling, persists immediately, then **swaps the row
  CONTENT in place** (text + tag) rather than removing and
  reinserting `ListViewItem` objects. Selection moves to the row
  that now holds the user's data.
- Calls `MainForm.RefreshNodeTree()` afterward so the tree reflects
  the new order.

**Why content-swap over item-move:** earlier attempts removed the
two `ListViewItem` objects and re-inserted them at swapped indices.
This worked for the data, but the Win32 listview's selection state
is keyed by row index, and the remove/reinsert dance was getting
mixed up with selection rendering. Swapping content keeps both row
objects in place — simpler, faster, no selection-state confusion.

### Delete-instance warning + cascade

In `MainForm.OnDeleteInstance`: pre-check whether the instance has
an existing (not just a stale ID for) restart rule. Different
confirmation message when a rule is involved. Cascade-delete the
rule entity in the same transaction. `engine.ReloadRules()` runs
afterward only when a rule was actually removed.

Stale `RestartRuleId` (entity missing) is treated as no-rule: no
special warning, no engine reload, no cascade attempted.

### Tree state preservation across refreshes

`MainForm.RefreshNodeTree()` previously did `Nodes.Clear()` +
rebuild, which collapsed the tree to its initial state and lost
the user's selection on every action that touched the DB. Fixed
with capture-and-restore:

- `CollectExpandedTags(nodes, tags)` — recursive walk gathering
  Tag values of expanded nodes BEFORE the clear
- `RestoreExpandedTags(nodes, tags)` — recursive walk after the
  rebuild, re-expanding any node whose tag was captured
- `FindNodeByTag(nodes, tag)` — recursive search to find the new
  TreeNode for a previously-selected tag (so `SelectedNode = X`
  can restore selection)

Tag values are stable across rebuilds even though `TreeNode`
references aren't — perfect identity key.

Instance loop in `RefreshNodeTree` also picked up the
`OrderBy(SortOrder).ThenBy(CreatedUtc)` so the tree mirrors the
InstallationPanel's instance order — without this, even a
refreshed tree would show instances in raw insert order.

### Critical: AfterSelect suppression during programmatic restoration

The single nastiest bug of the session. Reproduction:

1. User clicks Up on a row in InstallationPanel's listview
2. `OnReorderInstance` swaps content, sets selection on moved row
3. Calls `RefreshNodeTree()`
4. RefreshNodeTree captures expanded tags + selected tag, clears
   tree, rebuilds, then assigns `_treeView.SelectedNode = X` to
   restore selection
5. **That assignment fires `AfterSelect`** (WinForms TreeView
   doesn't have a no-event variant)
6. `TreeView_AfterSelect` matches `installation:X` and calls
   `ShowPanel(New InstallationPanel(X))`
7. `ShowPanel` disposes the current InstallationPanel and creates
   a brand new one
8. The fresh panel has NO listview selection — the user's
   just-set selection is gone

Fix: `_suppressTreeAfterSelect` flag on MainForm. Set true around
the restoration `SelectedNode` assignment, checked at the top of
`TreeView_AfterSelect` for early return.

**Bonus side effect:** every previous `RefreshNodeTree` callsite
(EditInstance save, EditInstallation save, AddNode, AddInstance,
Delete operations, etc.) was previously rebuilding the entire active
panel even when the entity hadn't changed. The fix makes all of
those snappier — panels only rebuild when the user actually
navigates to a different node.

**Lesson learned:** when a bug seems impossible to diagnose,
diagnostic instrumentation beats theory crafting. The whole
session's worth of focus / `HideSelection` / `Show()` / `Activate()`
theories were all wrong; a single MessageBox at the top of
`OnReorderInstance` showing `SelectedItems.Count` revealed in 30
seconds that the count was 0 when it should have been 1, which
pointed straight at "something between the handler exit and the
next handler entry is clearing selection."

### State-driven Start/Stop/Restart buttons (InstancePanel)

New cached field `_latestProcState` updated on every state observation.
New method `RefreshButtonsFromState()` drives button enabled-state:

- `Running` → Stop + Restart enabled, Start disabled
- `Stopped` / `Crashed` / `CrashLoopHalted` → Start enabled, others disabled
- Transitional states (`Starting` / `Stopping` / `Updating`) → all disabled
- Unknown / `WaitingForInput` → all disabled (safe default)

Called from end of `ApplyProcessState` (3-second refresh tick) and
Finally blocks of click handlers. The old `SetButtonsEnabled(True)`
in Finally was wrong — e.g. after a successful Stop, it'd re-enable
the Stop button which should be disabled.

### Execution history details column (AutomationRulesForm)

Replaced the 50-char hard-truncated raw JSON with
`FormatExecutionDetails(exec)`:
- If `SkipReason` is set → show that
- Else deserialize `ActionResultJson` as `ActionResult` and show its
  `Message` field
- Fallback: 80-char-truncated raw JSON if parse fails

Widened Details column 220→290 px. Shrunk Rule column 150→100
(it shows GUIDs anyway).

**Subtle gotcha avoided:** initial implementation used
`Dictionary(Of String, Object)` parse — STJ boxes values as
`JsonElement` and `.ToString()` on a string-kinded `JsonElement`
returns content without quotes on .NET 8 but the behavior varies
by version. Switched to direct `ActionResult` deserialization for
version-stable behavior.

---

### AutomationRulesForm — modal → non-modal singleton

Was opened with `ShowDialog()` from the Tools menu, the tree-root
click, and the EditInstanceForm drift redirect. Three reasons it
should be non-modal:

1. Live-updating execution history — you want to keep it open and
   watch rules fire from elsewhere
2. Matches History window precedent
3. Rule firing happens in the background; modal blocks the user
   from doing anything else while inspecting

Fix in MainForm:
- New `_automationWindow` field tracking the singleton
- `OnAutomationRules()` made `Public`. Brings existing window to
  front (un-minimize + Activate) if open; otherwise creates,
  hooks `FormClosed` to drop reference, then `Show(Me)` for
  owner-coupling.
- `EditInstanceForm.OnOpenInAutomationRules` finds MainForm via
  `Application.OpenForms.OfType(Of MainForm)()` and calls
  `OnAutomationRules` — routes through the singleton path.

**Owner-coupling rationale:** `Show(Me)` makes the window stay above
MainForm in z-order. Without owner, clicking the tree-root
"Automation Rules" node would open the window but it'd immediately
go behind MainForm — the tree click event continues dispatching
back through MainForm, which steals focus. Owner-coupling sidesteps
the race entirely. Side effect: minimizing MainForm minimizes the
child too. Acceptable trade-off given the alternative was the
window disappearing on click.

### Tree-click race for non-modal child windows — lessons

General pattern: when a non-modal window is opened from a click
handler ON A CHILD CONTROL of MainForm (tree node, button on a
panel), the click event continues dispatching after the new window
appears, and MainForm steals focus back at the end of dispatch.
Fix is owner-coupling (`Show(Me)`) for windows that should stay
above MainForm, OR `BeginInvoke(Activate)` deferral for windows
that should be peers.

History window uses no-owner because users want it independent of
MainForm minimization. AutomationRulesForm uses owner-coupling
because it's reachable from a tree node and the alternative is
losing it on every click.

---

### Phase 4b-1 — RuleEditorForm rewrite shell (closed)

Replaced the stub editor with a real one. Power users can
now author single-action rules covering all 5 scopes, all 4
trigger types, and any of the 11 leaf action types via a
dropdown-driven form. Conditions section is a placeholder
(deferred to 4b-2). SequenceAction is excluded from the
action picker (deferred to 4b-3) but rules with existing
sequences load with a warning and round-trip the sequence
untouched on save.

**New file:** `GSM.Manager\UI\RuleEditorForm.vb` (~1100 lines)

**Form layout** (FixedDialog 760×800):
- **Rule** group: Name, Enabled, Scope (5 values), Game filter,
  Target combo (varies by scope), Overlap policy
- **Trigger** group: type picker + sub-editor for the
  selected type (Schedule with cron preview & presets,
  StateChange with from/to combos, VersionMismatch and
  Manual as info-only)
- **Conditions** group: placeholder text only — existing
  conditions on a rule are preserved across save in
  `_preservedConditions` and re-serialised untouched
- **Action** group: type picker + sub-editor for the
  selected type (11 builders covering coordinated_restart,
  start/stop/restart_instance, start/stop_all_instances,
  update_installation, send_rcon, notify, wait,
  wait_for_ready)
- Save / Cancel

**Sub-editor pattern:** every trigger / action type has a
`Build*Editor()` method returning a `TriggerSubEditor` /
`ActionSubEditor` record:
```vb
Friend Class TriggerSubEditor
    Public Property Panel As Panel
    Public Property BuildFn As Func(Of ITrigger)
    Public Property LoadFn As Action(Of ITrigger)
End Class
```
The lambdas close over the panel's controls so the form
doesn't need per-type field storage. `OnTriggerTypeChanged`
/ `OnActionTypeChanged` clears the host panel, dispatches
to the right `Build*Editor()` method, and mounts the
resulting Panel.

**Key VB.Net gotcha** (added to the gotcha table): lambdas
that construct a concrete `ScheduleTrigger` / `StartInstanceAction`
/ etc. infer their return type as `Func(Of ConcreteType)`,
which does NOT fit `Func(Of ITrigger)` / `Func(Of IAction)`
slots. Two workarounds:
- Single-expression lambdas: wrap the return in
  `CType(..., ITrigger)` / `CType(..., IAction)`.
- Multi-line lambdas with branching: use explicit return
  type — `Function() As ITrigger ... End Function`.

Used CType for the simple cases (most builders) and
explicit return type for StateChangeTrigger and NotifyAction
whose construction logic branches.

**Target combo behaviour by scope:**
- Instance / Installation / Node → `DropDownList` of `IdItem`
  entries pre-populated from the cached lookup data
- InstanceSet → `DropDown` (allows free-form text) with
  AutoCompleteMode = SuggestAppend pulling from existing
  distinct tags. Free-form lets users target a tag they're
  about to create.
- AllInstances → label and combo hidden

The target combo's `DropDownStyle` is toggled in
`OnScopeChanged` and contents fully cleared & repopulated
on every scope change.

**SequenceAction round-trip in 4b-1:** when a rule's action
is a SequenceAction, the action picker is disabled and a
warning label is shown in the sub-editor panel. The
sequence is stashed in `_preservedSequenceAction` and
written back unchanged on save. Other fields (Name,
Scope, Trigger, GameFilter, Overlap, Enabled) remain
editable. This means power users can adjust a coordinated
update rule's name / target without losing the sequence
steps before 4b-3 lands.

**Validation on save:**
- Name required
- Target required for non-AllInstances scope
- Cron expression required + parseable for ScheduleTrigger
- Action's required identifier present (instance,
  installation, or notification plugin per action type)
- SendRcon command non-empty; Notify message non-empty
- WaitAction needs no validation (DurationMs has a sensible default)

**Helper utilities (`Friend Shared`, callable from
future forms / sub-editors):**
- `GetSelectedId(combo)` — unwraps an IdItem from the
  combo's selected item
- `SelectComboById(combo, id)` — selects the item whose
  IdItem.Id matches; no-op if not found
- `ClampToRange(value, num)` — clamps an Integer into a
  NumericUpDown's Min/Max range so loading an out-of-
  range value doesn't throw at .Value assignment
- `IdItem` class — lightweight (Id, Display) item carrier
  for combo entries

**Lookup data caching:** form pulls all needed lookups in
one `LoadLookupData` pass at construction time:
`_instances`, `_installations`, `_nodes`,
`_notificationPlugins`, `_distinctSetTags`,
`_distinctGameIds`. All `AsNoTracking()`. Type-switch
handlers reuse this cached data instead of re-querying
on every dropdown change.

**Deferred to 4b-2 (conditions UI):** ConditionEditorForm
for adding/editing/removing conditions, ConditionMode
selector (All vs Any), per-condition sub-editors for the
3 condition types. Currently the Conditions section just
shows a placeholder string and `_preservedConditions`
holds the deserialised list across save.

**Deferred to 4b-3 (sequence editor):** SequenceAction
sub-editor with reorderable step list; StepEditorForm
modal for editing one step; re-enable SequenceAction in
the action picker dropdown.

**Files modified:**
- New: `GSM.Manager\UI\RuleEditorForm.vb` (~1100 lines)
- Modified: `GSM.Manager\UI\RemainingForms.vb` — old stub
  RuleEditorForm class removed (~160 lines deleted)

No changes to AutomationRulesForm — its
`Using dlg As New RuleEditorForm()` calls work unchanged
because the new class has the same name + same constructor
signature (`Optional editRuleId As String = Nothing`) in
the same namespace.

---

### Phase 4b-3 polish — Tabbed layout (closed)

Final layout pass on RuleEditorForm. The form had grown to
~935px tall with all four sections (Rule / Trigger /
Conditions / Action) stacked vertically — pushing against
1080p comfort and forcing scroll on smaller displays.
Reorganised into a tabbed layout that drops form height to
~480px and groups fields functionally instead of by
typography.

**Layout:**
```
┌─ Edit Rule ────────────────────────────────────────────┐
│ Name: [...........................] [✓] Enabled        │  ← Header strip (always visible)
│ ──────────────────────────────────────────────────────│
│ ┌──────┬─────────┬────────────┬────────┐              │
│ │ Rule │ Trigger │ Conditions │ Action │              │  ← Tabs
│ └──────┴─────────┴────────────┴────────┘              │
│ ┌────────────────────────────────────────────────────┐ │
│ │  (selected tab's content here)                     │ │  ← One tab visible at a time
│ └────────────────────────────────────────────────────┘ │
│                                    [Save] [Cancel]    │
└────────────────────────────────────────────────────────┘
```

**Header strip — Name + Enabled:** Lives outside any tab
because they're the rule's identity (referenced from every
tab) and a global toggle (conceptually outside any one
section). Saves users a click when they just want to rename
or toggle.

**Tab contents:**
- **Rule:** Scope, GameFilter, Target, Overlap. Fields that
  say "what does this rule apply to."
- **Trigger:** Type combo + sub-editor. Sub-editor area
  expanded to ~240px tall (was 100px when stacked).
- **Conditions:** Mode + Add/Edit/Remove/↑/↓ + listbox.
  Listbox grew to ~240px tall (was 85px).
- **Action:** Type combo + sub-editor. Sub-editor area
  also ~240px tall, finally giving the sequence editor's
  listbox proper breathing room (~170px, visible ~10 rows).

All tabs sized to fit the largest. Smaller tabs have
whitespace below their content — acceptable since the tab
background is uniform and the bordered sub-panels visually
frame their content.

**Validation glyphs:** When Save fails, the broken tab gets
a "⚠ " prefix (Segoe UI U+26A0 — plain Unicode, renders
cleanly without font mixing) and the form auto-switches to
that tab so the user sees the inline error message in
context. Asymmetric "show only when broken" pattern —
adding ✓ checkmarks to good tabs every time would feel like
the form is grading you. The glyph stays until the next
Save attempt clears it.

`_plainTabCaptions As Dictionary(Of TabPage, String)` stashes
the original captions at construction so `ClearTabValidationGlyphs`
can restore them at the start of each Save attempt. The
`MarkTabBroken(tab)` helper is idempotent — calling twice
doesn't double the prefix.

**Validation order matches tab order** so the auto-selected
"first broken tab" is also the leftmost. Header (Name) →
Rule → Trigger → Action. Conditions tab has no save-time
validation (empty list is valid; per-condition validation
runs inside ConditionEditorForm).

**Header-strip Name validation** can't mark a tab (Name lives
outside the tab control) so it focuses the textbox directly
instead of switching tabs.

**Forms structure after this round:**
- RuleEditorForm.vb — ~1500 lines, tabbed layout
- ActionEditorFactory.vb — ~750 lines, 11 leaf-action
  builders + helpers
- StepEditorForm.vb — ~190 lines, single-step modal
- ConditionEditorForm.vb — ~440 lines, single-condition
  modal

**Note on file rewrites:** This was a complete rewrite of
RuleEditorForm.vb rather than incremental edits. The
structural change (form → header + tabs) touched the layout
section top-to-bottom, and surgical edits across that many
sections would have been more error-prone than a clean
rewrite. The handler/validation/sub-editor logic was
preserved verbatim — only the layout containers changed.

### Phase 4b-3 — Sequence editor (closed)

Final piece of the rule editor: full SequenceAction authoring
with reorderable step list and modal step editor. With this
phase, the rule editor is feature-complete for all 12 action
types. Users can compose multi-step coordinated operations
(announcement → wait → wait_for_player_count → update →
start all → notify) entirely through the UI.

**Three new architectural pieces:**

1. **ActionEditorFactory** (new file `GSM.Manager\UI\ActionEditorFactory.vb`,
   ~750 lines). Holds the 11 leaf-action builders previously
   private to RuleEditorForm. Constructor takes the lookup
   data (instances, installations, notification destinations).
   `BuildEditor(id) As ActionSubEditor` is the public
   dispatcher. Two `Public Shared` helpers: `GetActionTypeId`
   and `ValidateAction`, both moved out of the form so
   StepEditorForm can call them without going through the
   parent form. Row helpers (AddInstanceComboRow, etc.) are
   private instance methods on the factory.

2. **StepEditorForm** (new file `GSM.Manager\UI\StepEditorForm.vb`,
   ~190 lines). Modal that mirrors ConditionEditorForm's
   shape: type combo + sub-panel + Save/Cancel. Constructor
   takes the factory; uses it to build whichever leaf-action
   sub-editor matches the chosen type. Type combo excludes
   "sequence" — no nested-sequence UI even though the
   serialiser supports nesting (use cases rare, UX gets
   unwieldy).

3. **Sequence sub-editor in RuleEditorForm** (~150 lines
   added). Lives in the form (not the factory) because it
   needs access to mutable `_sequenceSteps` state and the
   StepEditorForm modal launcher (factory creating the modal
   would be a circular dependency). Layout fits inside the
   existing 690×145 sub-panel: top row = Add/Edit/Remove/↑/↓
   buttons + ContinueOnFailure checkbox; below = step listbox.

**File-size delta:**
- RuleEditorForm.vb: ~84KB → ~69KB (removed ~600 lines of
  builders + helpers that moved to factory; added ~150
  lines of sequence editor)
- New: ActionEditorFactory.vb (~28KB)
- New: StepEditorForm.vb (~8KB)

**Step summary format** (one line per step in the listbox):
```
1. Notify PowerGSM #test: "Restart in 5 min"
2. Wait 240000ms
3. Notify PowerGSM #test: "Restart in 1 min"
4. Wait 60000ms
5. Coordinated Restart: LOP_Site-Main_S01
```
Numbers are 1-based to match the engine's "Step 1/N" log
progress messages. Long messages truncate at 30 chars +
"..." so the listbox stays readable. Looked-up names for
instance/installation/destination references; falls back to
raw ID if entity was deleted.

**Sequence validation on save:**
- Sequence must have at least one step (else "Sequence must
  have at least one step. Click Add to create one, or pick
  a different action type.")
- Each step is validated via `ActionEditorFactory.ValidateAction`
  individually; first failure stops with `"Step N: <error>"`.
  Per-step validation also runs when the step modal saves,
  so this is a defensive double-check (covers the case where
  the step was authored, then a referenced entity was
  deleted between authoring and rule save).

**Edit-mode round-trip:** previously the form gray'd out
the action picker and showed a warning when the rule had a
sequence ("editor lands in 4b-3"). Now it just loads the
steps into _sequenceSteps via the editor's LoadFn and lets
the user edit normally.

**Sub-editor-instance handler binding pattern:** the step-list
buttons need a way to find the active listbox without
storing it as a form field (it's transient sub-editor state
that shouldn't outlive a type-change). Solution: stash the
listbox reference in the sub-editor panel's `Tag` property.
`GetSequenceListBox()` retrieves it via the form's
`_currentActionEditor.Panel.Tag`. If the user has switched
away from the sequence type, the handlers no-op rather than
mutating dead state.

**Visibility of helper types:** `IdItem` and `ActionSubEditor`
became `Public` (from `Friend`) because the factory needs
them from a different file. Same project so `Friend` would
work too, but `Public` is safer if anything ever moves to
a different namespace.

**Out of scope (deliberately not added):**
- Step duplication ("Duplicate this step" button) — Add+Edit
  replicates the data anyway
- Drag-to-reorder — Up/Down buttons match conditions UX
- Live "sequence will take ~N minutes" preview — wait and
  wait_for_player_count have known durations, but
  wait_for_ready and update_installation don't
- Nested-sequence UI in StepEditorForm — serialiser
  supports it; if a power user really wants it they can
  hand-edit the JSON
