# Phase 5d-7 — Surfacing the Discord command model in the Manager

## Status

`[shipped]` — plan written 2026-05-29; open questions resolved the
same day (see Decisions). Implemented 2026-05-30 (5d-7a + 5d-7b +
5d-7c; 5d-7d deferred per Decision #1). See "Implementation notes
(shipped)" below for what landed and the one deliberate divergence
from the 7b sketch.
**Renumbered and reframed** — first drafted as "5d-3" against a
stale ROADMAP entry; that number collided with the completed
canonical Phase 5d-3 (role mapping UI + permission enforcement),
so this is now **5d-7**. See Background.

## Background — why this is renumbered and reframed

This work was first drafted as "5d-3" to match a ROADMAP entry
that read: *"Per-guild allowlist of instance IDs for slash
commands and panels… each guild sees only what it's been scoped
to. New table for scope config; Manager-side UI for setting scope
per guild."*

Two problems surfaced on investigation (2026-05-29):

**1. The number collided.** The canonical `Phase5d_Plan.md`
defines sub-phases 5d-1…5d-5, all completed and shipped (0.2.0).
Canonical 5d-3 was "role mapping UI + permission enforcement."
The ROADMAP had re-used 5d-2 / 5d-3 / 5d-4 for follow-on Discord
work. This entry is renumbered **5d-7** to continue the sequence
cleanly (display-format tweak → 5d-6, this → 5d-7, `/lastseen`
→ 5d-8).

**2. The premise was stale — scoping is already built.** Per-guild
scoping shipped as part of canonical 5d-4 (slash commands):

- `DiscordPanelEntity` already carries `GuildId` + `ScopeKind`
  (`allinstances` / `game` / `installation` / `instanceset`) +
  `ScopeTargetId`.
- `DiscordBotPlugin.GetInstancesVisibleInGuild(guildId)` already
  computes a guild's visible instance set as the **union of that
  guild's panels' scopes**.
- `/players` already enforces it — both the autocomplete and the
  explicit-instance-ID path reject anything outside the guild's
  visible set.
- Role permissions already exist: `ResolveUserPermission(member,
  guildId[, panelId])` over a role→`CommandPermission`
  (`Everyone` / `ServerOperator`) map, with guild-default plus
  per-panel overrides.

So per-guild scoping is **already built** — just derived from
panels rather than a separate table. The actual gap is the one
identified in conversation: the slash-command surface is an
**emergent side-effect** of panel scope + role permissions, and
**nothing in the Manager UI names it.** An operator configuring
a panel doesn't know they're defining what `/players` can see in
that guild; an operator mapping roles doesn't know they're
gating slash commands. The hub form (`DiscordBotForm`) shows
"Bot Setup / Panels / Role Mappings" and never says "command."

This phase closes that gap: make the command model **visible and
intentional**, in the UI and in the code.

## Current model (as built, for reference)

- **Commands:** `/help` (Everyone), `/panels` (Everyone),
  `/players` (ServerOperator+). `/lastseen` is Phase 5d-8, not
  yet built. Registered via the DSharpPlus SlashCommands
  extension (attributes on `GsmSlashCommands`), *not* via
  `INotificationPlugin.GetSupportedCommands` (which is the
  separate remote-command interface and returns empty).
- **Visibility:** `GetInstancesVisibleInGuild(guildId)` = union
  over the guild's `DiscordPanels` of each panel's
  ScopeKind/ScopeTargetId filter. A guild with no panels sees no
  instances via slash commands.
- **Permissions:** per-command minimum tier is currently
  expressed *inline* in each handler (e.g. `/players` does
  `If perm < CommandPermission.ServerOperator Then …`). There is
  no central catalogue of "command → required tier."

## Goal

Make the Discord slash-command model legible from the Manager:

1. The operator can see **which commands exist** and the
   permission tier each requires.
2. The operator understands that a guild's command **visibility
   = its panels' scopes**, and that **role mappings gate command
   access** — stated explicitly, where they configure those
   things.
3. The operator can **inspect per-guild effective access**: what
   instances a given guild's commands can see, and who can run
   them.
4. In code, the command→permission relationship becomes a single
   declarative source of truth instead of inline checks (so the
   UI display can't drift from actual enforcement).

## Design

Four parts, smallest-first. 5d-7a–c are the phase; 5d-7d is a
deferred decision.

### 5d-7a — A command catalogue as single source of truth

Extract the per-command metadata that's currently implicit/inline
into one declarative table, consumed by three places: the command
handlers (for gating), the Manager Commands surface (for display),
and `/help` (which already lists commands and can now read the
same source).

- New `SlashCommandCatalog` (a small `Friend` table in
  `GsmSlashCommands.vb` or a sibling file): each entry =
  command name, human description, `CommandPermission` minimum,
  and a one-line "what it sees" note.
- `/players`'s inline `ServerOperator` check reads its minimum
  from the catalogue instead of a literal. `/help` renders from
  the catalogue. Each command's description lives in a `Const`
  referenced by *both* the `<SlashCommand>` attribute and the
  catalogue entry, so the description has a single source and
  can't drift (Decision #4).
- This is the "make it intentional in code" half — it turns the
  command model from scattered literals into a named thing the
  rest of the phase can display.

### 5d-7b — Inline awareness in the existing editors

Pure labels; no behaviour change. The cheapest, highest-value
discoverability win.

- `DiscordPanelEditorForm` — explanatory text by the scope
  field: *"Instances in this panel's scope are also what slash
  commands (`/players`, and `/lastseen` once added) can see in
  this guild."*
- `DiscordRoleMappingsForm` / `DiscordPanelRoleOverridesForm` —
  text: *"These role mappings also govern who can run slash
  commands in this guild, not just panel buttons."*

### 5d-7c — Commands surface + per-guild effective-access preview

A dialog launched from a "Commands & Access…" button on
`DiscordBotForm` (matching the existing Role Mappings
button→form pattern; Decision #2), with two read-only views:

- **Commands list** — rendered from the 5d-7a catalogue: each
  command, its description, its required permission tier. Static
  per build; no per-guild logic. Answers "what can the bot even
  do."
- **Per-guild effective access** — a guild dropdown (populated
  from `DiscordBotPlugin.GetGuildsAndChannels`) that, on
  selection, shows:
  - the effective visible-instance set
    (`GetInstancesVisibleInGuild`), and
  - the guild-default role→permission map — which is the
    *complete* answer for commands: verified that slash commands
    resolve against the guild-default map only, so per-panel
    overrides (which gate panel buttons) don't apply here
    (Decision #3).
  This is the diagnostic that's impossible today: "for THIS
  guild, what do the commands see and who can run them."

### 5d-7d — Explicit per-guild command scope, decoupled from panels (DEFERRED — decision)

Only if the panel-derived coupling proves insufficient. Two
scenarios it would unlock:

1. Slash commands for instances that have **no persistent
   panel** in the guild (today: no panel → no visibility).
2. Slash visibility as a **subset** of what the panels show
   (today: visibility *is* the panel union, exactly).

Would require a per-guild command-scope entity, a
`GetInstancesVisibleInGuild` that consults it (instead of / in
addition to panels), and a Manager editor for it.

**Lean: defer.** The panel-derived coupling is a sensible
default — if you've surfaced instances in a guild's panel, it's
reasonable that the guild's commands can query them. Surface the
coupling first (7a–c); only build the decoupling if a real need
appears. Captured here so the option isn't lost.

## Touch points

- **`GSM.Manager\Core\GsmSlashCommands.vb`** — add the
  `SlashCommandCatalog`; `/players` and `/help` read from it
  (5d-7a).
- **`GSM.Manager\UI\DiscordPanelEditorForm.vb`** — scope-field
  explanatory label (5d-7b).
- **`GSM.Manager\UI\DiscordRoleMappingsForm.vb`** and
  **`DiscordPanelRoleOverridesForm.vb`** — permission-scope
  explanatory labels (5d-7b).
- **`GSM.Manager\UI\DiscordBotForm.vb`** — a "Commands & Access…"
  button launching a new dialog (the Commands list + per-guild
  effective-access preview, 5d-7c). The dialog reads
  `GetInstancesVisibleInGuild` and `GetGuildsAndChannels` (both
  already exist on `DiscordBotPlugin`); may need a small
  `Friend` accessor for the guild-default role map if one isn't
  already exposed.
- **(5d-7d only)** `GsmDbContext.vb` new entity +
  `GetInstancesVisibleInGuild` change + new editor form.

## Decisions (resolved 2026-05-29)

Walked and confirmed. Two moved off their initial lean: Q2
(placement) and Q3 (sharpened on a verified code fact).

1. **Decouple command scope from panels (5d-7d) — DEFER.** The
   panel-derived coupling is a sensible default and 7a–c address
   the actual gap. Build 7d only if a real need surfaces — most
   likely trigger: a guild wanting `/players` / `/lastseen` for
   instances with no persistent panel.

2. **Commands surface placement — DIALOG** (changed from the
   "section" lean). A "Commands & Access…" button on
   `DiscordBotForm` opens its own dialog, consistent with the
   existing Role Mappings button→form pattern. The interactive
   per-guild preview earns its own window rather than crowding
   the hub form.

3. **Effective-access preview depth — GUILD-DEFAULT ONLY**
   (verified correct, not merely simpler). Confirmed in code:
   slash commands call `ResolveUserPermission(member, guildId)`
   with no panelId, resolving against the guild-default role
   map. Per-panel overrides apply only to panel-button
   interactions, never to slash commands. So the preview shows
   guild-default visibility (union of panel scopes) + the
   guild-default role map = exactly what governs commands;
   per-panel overrides are out of scope for this surface.

4. **Catalogue scope — GATING + DISPLAY ONLY.** DSharpPlus
   registration descriptions must be compile-time constants, so
   a runtime catalogue can't drive them; `<SlashCommand>`
   attribute strings stay as-is. Each description lives in a
   `Const` referenced by both the attribute and the catalogue
   entry (single source, no drift). Plugin-contributed commands
   would need dynamic registration built off this catalogue —
   a Phase 7 / far-future concern, noted only so the catalogue
   isn't a dead end.

5. **`/help` reads from the catalogue — YES.** Once 7a lands,
   point `/help` at it so Discord and the Manager Commands
   surface render identical command info from one source.

## Implementation notes (shipped 2026-05-30)

All three sub-phases landed in one pass; 5d-7d remains deferred.

**5d-7a — SlashCommandCatalog.** Added `Friend NotInheritable Class
SlashCommandCatalog` to `GsmSlashCommands.vb` (namespace
GSM.Manager.Core). Per-command `Const` Name/Description fields are
shared by the `<SlashCommand>` attributes and the catalogue rows
(Decision #4 — no drift). A `CommandEntry` row = Name, Description,
MinimumPermission, VisibilityNote. `/players` gating now reads
`Find(PlayersName).MinimumPermission` (the denial message
interpolates the tier off the same entry); `/help` renders by
looping `All` with a `PermissionTag` helper. Registered
descriptions kept byte-identical, so no Discord re-registration
churn. Row type named `CommandEntry` (not `Entry`) to avoid a
case-insensitive clash with `entry` locals.

**5d-7b — inline awareness labels.** Pure labels, each hosted in
its form's existing hint cell (taller row + a stacked sub-panel)
to avoid renumbering fixed-grid rows. `DiscordPanelEditorForm`:
grey hint under the scope-target combo (scope = slash visibility).
`DiscordRoleMappingsForm`: static note that guild-default mappings
also gate slash commands.

  **Divergence from the 7b sketch (deliberate).** The sketch
  proposed the SAME label for both role forms. That is wrong for
  `DiscordPanelRoleOverridesForm` per the plan's own verified
  Decision #3 — per-panel overrides never affect slash commands.
  So the overrides form carries the OPPOSITE note: "Slash commands
  are NOT affected by panel overrides — they always use the
  guild-default role mapping." This keeps the surface honest with
  enforcement and reinforces the visibility-vs-permission
  distinction the phase exists to make.

**5d-7c — Commands & Access dialog.** New
`DiscordCommandsAccessForm` launched from a "Commands & Access…"
button on `DiscordBotForm` (button→form pattern, Decision #2).
Read-only: a catalogue-driven Commands list (uses VisibilityNote)
plus a per-server preview pairing `GetInstancesVisibleInGuild`
with the guild-default role map (`DiscordRoleMappings` PanelId =
"", read directly from the DB — no new plugin accessor needed).
Guild-default only, never per-panel overrides (Decision #3).
Empty states spell out the consequence (no panels → commands see
nothing; no elevations → only Everyone-tier commands usable).

No schema/migration changes. No `.vbproj` changes — the SDK
auto-discovers the new UI file.

## Cross-references

- **ROADMAP reconciled (2026-05-29).** The 5d series in the
  ROADMAP had drifted — panels, slash commands (`/help`,
  `/panels`, `/players`), autocomplete, role permissions, and
  per-guild visibility are all built (canonical 5d-1…5d-5,
  shipped 0.2.0) but were reading as future work, and the
  follow-on items had collided onto 5d-2/5d-3/5d-4. Fixed:
  follow-on work renumbered 5d-6 (display format, shipped),
  5d-7 (this), 5d-8 (`/lastseen`); a note in Recently-shipped
  records that canonical 5d-1…5d-5 shipped in 0.2.0.
- **Phase 5d-8 (`/lastseen`)** becomes cleaner after this: it
  registers in the 5d-7a catalogue, inherits the documented
  visibility + permission model automatically, and shows up in
  the Commands surface and `/help` with no extra UI work.
- **Phase 5k (player-list panel)** is a panel *type*; its guild
  scoping rides the same `DiscordPanelEntity` model, so it
  inherits the visibility coupling this phase documents.
- **Relationship to permissions vs. visibility:** these are two
  independent axes that this phase keeps distinct — *visibility*
  (which instances, from panel scopes) and *permission* (who can
  run, from role maps). The surface in 5d-7c shows both side by
  side precisely so the operator stops conflating them.
