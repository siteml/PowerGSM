# Phase 5d — Discord Bot Integration

Design document for adding a full Discord bot plugin alongside the
existing Discord webhook integration. Read this first in the new chat;
everything below assumes the conversation is starting fresh.

---

## Goal

Add a bot-driven Discord experience built around **persistent
interactive control panels** — one or more rich-embed messages
the bot maintains in operator-chosen channels, each showing a
configurable table of instances (status, players, next scheduled
restart) and offering start/stop/restart/update actions to users
with the right Discord roles. Public viewers see the table as
read-only information; operators get gated management UI via
ephemeral followup messages.

Slash commands and outbound event notifications via the bot
remain part of the design but are secondary — the control panels
are the headline feature.

User-facing capabilities being added:

1. **Persistent control panels.** Operator configures one or
   more panels: each lives in a chosen Discord channel, shows
   a filtered set of instances (one game / one installation /
   an InstanceSet tag / explicit list), refreshes its embed
   contents as instance state changes. Anyone in the channel
   sees current status; operators can act.

2. **Ephemeral management UI.** Authorised users click "Manage"
   on the panel, get a private message visible only to them
   with: a dropdown listing the panel's in-scope instances, an
   action selector (Start/Stop/Restart/Update). Permission gate
   runs at click time — unauthorised users get a "no access"
   response that nobody else sees.

3. **Discord-native time formatting.** Next-scheduled-restart
   cells emit Discord timestamp tags (`<t:UNIX:F>` /
   `<t:UNIX:R>`) so each viewer sees the time in their own
   timezone, automatically.

4. **Optional outbound notifications.** Bot can also send
   event-driven notifications, identical functionality to the
   existing webhook plugin but with richer formatting. Coexists
   with webhooks; operators pick which transport per
   destination.

5. **Optional slash commands.** Fallback for things the panel
   doesn't cover (e.g. `/players <instance>` to see a full
   player list when the panel only shows counts).

---

## Honest assessment of the current infrastructure

### What's already there and reusable

The existing `INotificationPlugin` contract carries roughly 70%
of the bot's plumbing:

- `SendNotificationAsync(NotificationContext)` — outbound. Bot
  plugin implements this exactly the same way the webhook plugin
  does, for the outbound notification path.
- `GetSupportedCommands()` returning `RemoteCommandDescriptor[]` —
  inbound. Bot returns slash commands here. Webhook returns empty.
- `IRemoteCommandHandler` — manager-side interface that bot
  plugins call to dispatch received commands. Already implemented.
- `InboundCommand` / `CommandResult` DTOs — already defined.
- `CommandPermission { Everyone, ServerOperator, Administrator }`
  — already defined.
- The Notifications UI form for destination config — extend with
  a new tab rather than replace.
- `PluginRegistry` already loads notification plugins via Roslyn,
  same as game plugins.

### What's missing and needs to be built

1. **Persistent gateway connection lifecycle.** Webhooks are
   HTTP-once-and-done. A bot maintains a long-lived WebSocket
   connection, has to handle reconnects, heartbeats, sequence
   resumption. The bot library handles most of this but the
   plugin needs to manage start/stop cleanly without blocking
   manager startup if the bot can't connect.

2. **Persistent panel messages.** Each configured panel has a
   Discord message ID stored alongside its config. On startup
   the bot edits the existing message in place (preserving
   message permalinks) rather than re-posting. If the message
   no longer exists (operator deleted it manually, channel
   purged), the bot detects the 404 on edit and re-posts.

3. **Panel state refresh.** The panel table needs to reflect
   current state. Hybrid approach:
   - **Event-driven** for state changes (instance start/stop,
     crash, update completion) — `NotificationEmitter` already
     fires these events; bot subscribes and triggers a panel
     refresh for any panel whose scope includes the affected
     instance.
   - **Timer-based** every 60s for player count drift and
     "next scheduled restart" countdowns — things that change
     without firing events.
   - Refresh budget: at most one Discord edit per panel per
     5 seconds to avoid Discord's per-channel rate limit, even
     if events fire faster. Coalesce pending refreshes.

4. **Ephemeral interaction flow.** Click "Manage" → bot creates
   an ephemeral followup message with instance dropdown.
   Dropdown selection triggers another ephemeral edit showing
   action buttons for the picked instance. Action click runs
   permission check + dispatches to `IRemoteCommandHandler`.
   Each step is its own Discord interaction; the bot maintains
   per-interaction state.

5. **Slash command registration.** Bot tells Discord on startup
   which slash commands it supports. Per-guild registration
   (instant propagation; re-register on every startup, cheap).

6. **Role-to-permission mapping.** Per Discord guild: which
   roles map to ServerOperator vs Administrator. Stored as
   configuration; UI surface needed.

7. **Multi-guild support.** One bot user can be in many Discord
   guilds. Each guild has its own roles, channels, panels.

8. **Panel configuration UI.** New tab in the existing
   Notifications form: list of panels, add/edit/delete. Adding
   a panel: pick guild → pick channel → pick scope filter →
   save → bot posts the panel message and stores the message
   ID.

Nothing is architecturally novel; the existing contract carries
it. The lift is mostly implementation detail of one new plugin.

---

## Resolved design decisions (from planning conversation)

### D1. Panel UX is "public table + ephemeral management"

Public message: read-only table + single "Manage" button. Click
Manage → ephemeral message visible only to clicker, with
dropdown + action buttons. Unauthorised clicks get an ephemeral
"insufficient permission" response.

This sidesteps Discord's 25-component-per-message limit (public
message stays trivial), keeps the channel uncluttered for
non-operators, and means unauthorised users never see UI they
can't use.

### D2. Multiple panels per guild

Each panel has independent scope. One panel per logical
grouping ("LO realms", "Factorio servers", "test installations").
Stored as separate `DiscordPanel` rows; bot tracks each
independently.

### D3. Hardcoded panel layout for v1

Columns: instance display name, status (icon + text), player
count, next scheduled restart (as Discord timestamp tag).
Action set: Start, Stop, Restart, Update — fixed.

Per-panel customisation in v1 is limited to: target channel,
scope filter, refresh interval. Column/button customisation
deferred to v2 if anyone asks.

### D4. State refresh: event-driven + timer hybrid

Subscribed to `NotificationEmitter` for state-change events
(instant refresh on instance start/stop/crash/update). Timer
every 60s for player counts and time-relative cells. Refresh
budget: max one Discord edit per panel per 5s, coalesce pending
refreshes.

### D5. Slash commands as supporting feature, not v1 headline

Phasing puts panels first, slash commands later. Initial slash
command set when added: `/players <instance>` (full player list
when panel only shows count), `/help`. Action commands
(`/restart`, `/stop`, `/start`) deferred — the panel's
management UI covers those. Adding them later costs nothing
since the dispatch goes through the same `IRemoteCommandHandler`.

---

## Clarifying questions (resolve before starting)

These affect implementation shape, not just details. Worth
answering before the first line of code.

### Q1. Bot library

Two mature .NET options:

  **(a) DSharpPlus.** Async-first API, clean modern style,
  slash command framework built in via
  `DSharpPlus.SlashCommands`. Active development, .NET 8
  supported. MIT-licensed.

  **(b) Discord.Net.** The other big one. Slightly older API
  style, command framework via `Discord.Net.Interactions`.
  Equally battle-tested, .NET 8 supported. MIT-licensed.

Functionally similar; idiomatic differences. **My recommendation:
DSharpPlus.** Async API matches our codebase style; the slash
command framework's attribute-driven registration is clean to
read; no strong reason to prefer Discord.Net unless you have
prior experience with it.

**Decision needed:** which library?

### Q2. Coexist with the webhook plugin, or eventually retire it?

  **(a) Coexist permanently.** Webhook plugin stays for users
  who prefer the simplicity. Bot is the "feature-rich" option.

  **(b) Eventually retire webhook.** Once the bot is stable,
  deprecate the webhook plugin.

Webhooks are simpler to set up — paste a URL, done. The bot
needs a Discord application, OAuth invite, role config. Setup
friction is real.

**My recommendation: (a)** for the foreseeable future.
Maintenance cost of keeping the webhook plugin is low (it's
working, a few hundred lines, serves a legitimate "I just want
notifications, no commands" use case). The bot provides
additional capability for users who need it.

**Decision needed:** keep webhook indefinitely or plan to retire?

### Q3. Multi-guild scope per bot

  **(a) One bot serves many guilds.** Standard Discord pattern;
  guild ID is just a column in the panel/role config tables.

  **(b) One bot per guild.** Simpler config but operator runs
  many bot applications.

**My recommendation: (a).** Industry standard. Implementation
cost is small.

**Decision needed:** confirm (a)?

### Q4. Slash command registration scope

  **(a) Globally.** Available in every guild; up to an hour
  propagation latency.

  **(b) Per-guild.** Instant propagation; re-register on
  startup, cheap.

**My recommendation: per-guild always.** Instant propagation,
straightforward, no special "dev mode" toggle.

**Decision needed:** registration strategy?

### Q5. Permission model

How does the bot decide whether a user can manage instances?

  **(a) Role-based.** Operator configures: "Discord role X in
  guild Y maps to ServerOperator permission." Standard
  expectation.

  **(b) Channel-based + role-based.** Role determines permission
  level, channel restricts where it applies. Defence in depth
  (a stolen account in the wrong channel can't act).

  **(c) Per-panel role overrides.** Each panel has its own
  role mapping for fine-grained control ("LO operators can
  manage LO panel; Factorio operators can manage Factorio
  panel").

**My recommendation: (a) for v1, (c) as v2 follow-on.** The
v1 case is "this guild has an ops role, mapping it to
ServerOperator covers it." Per-panel overrides are useful when
one guild hosts multiple games with different ops teams; that's
a real but niche case.

**Decision needed:** permission model for v1?

### Q6. Bot offline behaviour for outbound notifications

If the bot disconnects from Discord during an event:

  **(a) Drop.** Same as webhook plugin's current behaviour.

  **(b) Queue and retry.** Hold events in memory, replay on
  reconnect.

  **(c) Persist and retry.** Same as (b) but on disk.

**My recommendation: (a) for v1.** Match webhook plugin.
Reliability features can be added in 5d-5 if needed; the queue
sits inside the bot plugin without touching the rest of the
system. Note: control panels handle their own reconnect by
re-rendering on next refresh tick, so this only affects
event-driven outbound notifications, not the panel UX.

**Decision needed:** offline behaviour?

---

## Proposed phasing

Numbered phases below build on each other. Each ends at a
shippable state with the previous functionality intact. The
existing webhook plugin keeps working throughout.

### Phase 5d-1: Bot scaffold + read-only control panels [COMPLETED]

Get a bot connecting, posting/maintaining configurable read-only
panel messages with instance state. No actions yet.

**New plugin:** `DiscordBotPlugin` in
`GSM.Manager.Core/DiscordBotPlugin.vb`. Implements
`INotificationPlugin`. Uses DSharpPlus (per Q1). For now
`GetSupportedCommands` returns empty.

**New schema** (one EF migration):

- `DiscordBotConfig`: per-guild bot settings (guild ID, token
  encrypted via `CredentialService`).
- `DiscordPanel`: panel definition. Columns: PanelId, GuildId,
  ChannelId, MessageId (nullable until first post), DisplayName,
  ScopeType (AllInstances/Game/Installation/InstanceSet),
  ScopeTargetId, RefreshIntervalSeconds (default 60),
  CreatedUtc, UpdatedUtc.

**New plugin behaviour:**
- On `InitialiseAsync`: connect to Discord, log in as bot.
- On startup or panel-config change: for each `DiscordPanel`,
  render the embed, then either edit the existing message
  (if MessageId is set and reachable) or post a new one and
  store the resulting MessageId.
- On `NotificationEmitter` events affecting in-scope instances:
  trigger a refresh for matching panels (coalesced, max one
  edit per panel per 5s).
- Per-panel timer at the configured interval to handle
  player-count drift and time-relative cells.
- Panel rendering uses Discord embed syntax: title, table-
  formatted body (one row per instance), Discord timestamp
  tags for the next-restart column. One "Manage" button at
  the bottom (no handler yet — clicked, returns a
  placeholder ephemeral response).

**Configuration UI:** new "Discord Bot" tab in the existing
Notifications form. Two sub-sections:

- **Bot setup**: per-guild bot token field, "Test connection"
  button.
- **Panels**: list of configured panels, add/edit/delete
  buttons. Add: pick guild → pick channel (auto-discovered
  from guilds the bot is in) → pick scope (dropdown of
  filter type, then target picker) → name → save.

**Acceptance:** a configured panel appears in the right
Discord channel, table renders correctly, Manage button is
visible but does nothing useful. State changes (start/stop an
instance via the Manager UI) reflect in the panel within a few
seconds. No actions wired yet. Webhook plugin untouched.

### Phase 5d-2: Management ephemeral flow [COMPLETED]

The "Manage" button works. Authorised users get the dropdown
→ action buttons → command dispatch flow.

**Interaction sequence:**

1. User clicks "Manage" on the public panel.
2. Bot does an immediate permission check. If user has no
   ServerOperator role: ephemeral response "You don't have
   permission to manage instances on this panel." Stop here.
3. If authorised: ephemeral followup with content:
   - Header: "Managing panel: <PanelDisplayName>"
   - Dropdown: in-scope instances (display name + current
     state icon for each option)
   - No action buttons yet (need an instance picked first)
4. User selects instance from dropdown.
5. Bot edits the ephemeral message:
   - Header: "Selected: <InstanceName> — Status: <State>"
   - Action buttons row: Start, Stop, Restart. Enabled-state
     mirrors the manager's `InstancePanel.RefreshButtonsFromState`
     policy (canonical source in `UiPanels.vb`):
        Running                    → Stop + Restart
        Crashed                    → Start + Stop (Stop stays
                                     enabled to break crash-
                                     restart loops)
        Stopped / CrashLoopHalted  → Start
        Transitional               → all disabled
     Inapplicable buttons remain visible but greyed; the layout
     doesn't shift between states and the user sees a clear
     visual signal of what's currently allowed.
6. User clicks an action button.
7. Bot rechecks permission, then dispatches via
   `IRemoteCommandHandler.HandleCommandAsync` with the
   appropriate `InboundCommand`.
8. Bot edits the ephemeral message with the result: "✓
   Restarting <InstanceName>..." or "✗ Failed: <error>".

**Update action deferred.** The original plan included a fourth
button (Update) but it's been scrapped from the bot UI entirely.
Reasons: (a) Update targets an installation, not an instance,
and affects every instance on it — it's a different shape from
the per-instance Start/Stop/Restart actions. (b) SteamCMD updates
can require interactive SteamGuard codes; the manager UI handles
these with a popup, but plumbing that through Discord ephemerals
adds a chunk of complexity for what's already an admin-tier
operation. (c) Updates are sensitive enough that confining them
to the manager (where the operator can see logs, watch progress,
and respond to prompts directly) is the safer default. Phase 5d-3
brings proper Administrator-tier gating for the existing actions;
Update stays manager-only.

**Permission check uses a stub:** for v1, treat any user with
the role configured as "ServerOperator" in their guild's role
mapping as authorised. The mapping itself is configured in
phase 5d-3 — until then, hardcode a single role name for
testing (e.g. "PowerGSM Operator"). UI for proper mapping
ships in 5d-3.

**Acceptance:** a guild user with the test role can Manage a
panel, pick an instance, run actions; the actions actually take
effect on the manager (instance starts, stops, restarts).
Unauthorised users get the "no permission" ephemeral response
on Manage click.

### Phase 5d-3: Role mapping UI + permission enforcement [COMPLETED]

Replace the hardcoded role name from 5d-2 with proper config.

**New schema** (one EF migration):
- `DiscordRoleMapping`: per-guild role → permission entries.
  Columns: GuildId, RoleId, RoleName (snapshot for display),
  Permission (enum: ServerOperator | Administrator).

**Updated UI:** the Discord Bot tab gains a "Role mappings"
sub-section. Per guild: grid of role + permission pairs. Roles
auto-populated from Discord (the bot fetches available roles
on guild config). Add/remove rows, save commits to DB and
refreshes the bot's in-memory mapping cache.

**Updated bot behaviour:** on every interaction, look up the
acting user's roles via Discord, intersect with the mapping
for that guild, derive the highest permission tier. Action
buttons now respect proper tier gating (e.g. an installation-
level command requiring Administrator is rejected for users
with only ServerOperator). Update was scrapped from the bot UI
in 5d-2 (see that phase's notes); 5d-3 doesn't need to gate
it, but the tier infrastructure ships here so future
admin-tier actions (anything requiring more than
ServerOperator) can lean on it.

**Acceptance:** the test-role hardcode is gone; permissions
work entirely from config. Adding/removing role mappings via
the UI takes effect on the next interaction without a bot
restart.

### Phase 5d-4: Outbound notifications via bot + slash commands [COMPLETED]

Bot becomes a viable transport for the existing event-driven
notification path, and slash commands fill gaps the panel
doesn't cover.

**Outbound notifications:** `SendNotificationAsync`
implementation. Operator can configure notification
destinations against the bot just like they configure them
against the webhook plugin (via the existing Destinations UI).
Each destination is a guild + channel; bot posts a rich embed
on event.

**Slash commands:** small initial set, registered per-guild on
bot startup:

- `/help` — list available commands and panels visible in this
  guild
- `/players <instance>` — full player list (panel only shows
  count). ServerOperator+ permission.
- `/panels` — list panels in this guild and their channel
  locations (handy if the operator forgets where they put
  one)

Action-style slash commands (`/restart` etc) deliberately
deferred — the panel UI covers those, and adding them later
is trivial.

**Acceptance:** notifications arrive via bot when destinations
are configured against it. Slash commands respond correctly.
Webhook plugin still works in parallel.

### Phase 5d-5: Polish & advanced features [COMPLETED]

Ordered by Site's priority signal at the close of 5d-4:

1. **Pagination for >25 instances per panel.** [COMPLETED]
   Discord caps select-component options at 25; previously
   the Manage dropdown silently truncated with a "showing
   first 25 of N" note. Shipped with prev/next buttons
   beneath the dropdown and a "Page X of Y" indicator in
   the prompt header; single-page panels (≤25 instances) see
   neither, so the small-panel UX is unchanged. Page state
   is encoded in the button custom IDs
   (`gsm:page:{panelId}:{n}`); the new `HandlePageClickAsync`
   re-renders the ephemeral with the requested page via
   `UpdateMessage`. pageIndex is clamped inside
   `BuildManageEphemeralBuilder` so stale IDs (when instances
   are removed mid-flow) land on the new last page rather
   than producing an empty dropdown. No schema work, as
   planned.

2. **Outbound queue with bounded retry.** [COMPLETED]
   v1 was drop-on-failure to match the webhook plugin's
   behaviour. v2 retains transient-failed events in a
   bounded in-memory ring buffer (cap 100) per
   `BotDestinationQueue` and replays on the next worker
   tick. The worker drains the retry buffer ahead of
   fresh queue contents so event ordering is preserved
   across the failure boundary. On overflow, the OLDEST
   entry is dropped so a long Discord outage can't OOM
   the manager.

   Failure classification was tightened in the same pass:
   `SendWithBackoffAsync` now returns a `SendOutcome` enum
   with three buckets. 403 (Unauthorized), 404 (NotFound),
   and 400 (BadRequest) are permanent and dropped; the
   catch-all (rate limits, 5xx, network) is transient and
   buffered. Previously 404/400 would have spun in the
   buffer until eviction. Gateway disconnect (client-null
   path) is also treated as transient now — events buffer
   for the brief reconnect window instead of being
   silently dropped. Lives entirely inside
   `BotDestinationQueue` as planned; no schema, no UI.

3. **Custom panel composition.** [COMPLETED]
   Expanded scope from the original "column ordering"
   sketch — operators now pick which elements appear on
   each panel row, their order, free-text separators
   between them, and a whole-panel grouping option (none,
   by node, by game, by node-then-game). Eight element
   types ship: state icon, instance name, state text,
   player count, game-specific context, next restart, node
   name (new — driven by a batched node-name resolution in
   `ResolveInScopeInstances`), and free-text separator.
   Stored as JSON on `DiscordPanelEntity.LayoutJson` (NULL =
   default layout, byte-identical to v1) plus a
   `GroupingKind` discriminator string. Renderer walks the
   parsed element list per row and applies a sort + header-
   emit pass for grouping; truncation marker is
   structure-aware. Editor UI lives inline in
   `DiscordPanelEditorForm` — listbox + add/remove/up/down/
   reset buttons, group-by combo. Position-aware previews
   in the listbox match what the renderer outputs (first
   element's prefix is dropped). Picked structured
   element list over template strings as foreshadowed; the
   template-string variant is unlikely to be needed for
   v3 either.

   Edit-flow gotcha caught during testing: `DiscordBotForm`
   field-copies form output onto a freshly-fetched DB row
   instead of attaching the form's entity directly, and
   the new fields had to be added to that copy too —
   otherwise grouping/layout edits silently reverted on
   reopen. Add path was unaffected (it persists the form's
   entity directly).

4. **Per-panel role overrides** (Q5 v2 follow-on). [COMPLETED]
   Each panel carries its own role-to-permission map that
   takes precedence over the per-guild default. Schema:
   `DiscordRoleMapping.PanelId` column (NOT NULL with `""`
   sentinel for guild-default rather than nullable, since
   SQLite's `NULL ≠ NULL` semantics on composite PK columns
   would let multiple guild-default rows for the same role
   coexist). PK extended to `(GuildId, PanelId, RoleId)`.

   Resolver semantics ended up as **whole-mapping override**
   rather than the per-role override the plan implied: if
   any panel-scoped rows exist for `(guildId, panelId)`,
   they're authoritative — the guild-default is NOT
   consulted for that panel. This lets an operator deny a
   role at panel scope simply by omitting it; per-role
   override couldn't have done that.

   Wire format change: `gsm:action:{instanceId}:{action}`
   → `gsm:action:{panelId}:{instanceId}:{action}` so action
   clicks know their panel context. Strict 5-part parsing;
   stale buttons rendered before the change silently fail
   (one panel-refresh cycle, ~5s). UI is a separate
   `DiscordPanelRoleOverridesForm` modal opened via
   "Configure..." on the panel editor with a status hint
   reading "using guild default" or "N override(s) — guild
   default ignored for this panel".

   Bug found and fixed in the same change: the existing
   `DiscordRoleMappingsForm` queried
   `Where(GuildId, RoleId)` without a `PanelId` filter, so
   after the schema change its add/edit/remove/commit would
   non-deterministically land on either the guild-default or
   a panel-scoped override row. Now filters
   `PanelId = ""` everywhere.

5. **Bot uptime metric in manager UI.** [COMPLETED]
   New `_connectedSinceUtc` field on `DiscordBotPlugin`
   tracking the wall-clock of the most recent successful
   gateway connect, surfaced as `ConnectedSinceUtc`.
   `DiscordBotForm` gained a 1-second poll timer that
   re-runs `UpdateStatusLabel` — fixes a latent staleness
   bug along the way (previously the label stayed at
   "Connecting to Discord…" or "Saved. Reconnecting bot…"
   until close+reopen). Connected state shows two lines:
   "✓ Connected to Discord." plus "Connected for 2h 18m
   (since 14:23 local)." with a granularity-stepping
   `FormatUptime` helper (seconds → minutes → H/M → D/H).
   Reset to Nothing on disconnect/connect-failure so a
   reconnect produces a fresh counter rather than running
   through the gap.

6. **Action commands as slash commands.** Permanently
   scrapped per Site's Phase 5d-4 review: panel UX is the
   canonical action surface and a parallel slash-command
   path would duplicate gating with no UX gain.

Nothing in this list is blocking. Best executed after a
round of real-world testing against live LO and Factorio
instances — testing tends to reorder the priority list and
occasionally surfaces sharper polish items that aren't
listed here.

---

## What this changes for existing functionality

**Webhook plugin:** untouched. Continues to work for operators
who don't want to set up a bot. Coexists with the bot plugin —
operator can configure both, route different events to
different transports.

**Manager:** new plugin registered in DI, three EF migrations
(one per phase 1-3), new tab in Notifications UI. No changes
to AutomationEngine, NotifyAction, NotificationService, or the
rest of the notification plumbing — the bot plugin is just
another `INotificationPlugin` consumer of `NotificationContext`
plus a new control-panel subsystem inside the plugin itself.

**Other plugins:** zero impact. Game plugins emit events via
`NotificationEmitter`, which fans out to whichever plugins are
registered.

---

## Operator setup flow (for context — not implementation)

What the operator does once the bot plugin ships:

1. Go to discord.com/developers/applications, create a new
   application, add a bot user, copy the bot token.
2. Use Discord's OAuth URL generator to invite the bot to
   their guild with appropriate permissions (Send Messages,
   Use Slash Commands, Embed Links, plus Manage Messages so
   it can edit its own panel messages).
3. In PowerGSM Notifications form → Discord Bot tab: paste
   token, "Test connection" verifies the bot reaches Discord
   and lists the guilds it's in.
4. Configure role mappings: pick a guild, map roles to
   permission levels.
5. Add panels: pick guild → channel → scope → save. Panel
   appears in the channel.
6. Optional: configure outbound notification destinations
   against the bot (separate from panels, for event-driven
   one-shot notifications).

Token storage uses the existing `CredentialService` for DPAPI
encryption, same shape as Steam credentials.

---

## Suggested first turn in the new chat

Paste this document. State the answers to Q1–Q6. Pick a
starting phase (5d-1 is the obvious one — control panels are
the headline and everything else builds on the bot scaffold).

A reasonable opening:

> Read Phase5d_Plan.md. Q1=DSharpPlus, Q2=coexist, Q3=multi-guild,
> Q4=per-guild registration, Q5=role-based for v1, Q6=drop on
> failure for v1. Start with Phase 5d-1: produce the schema
> migrations, plugin scaffold, control-panel rendering, and
> the Discord Bot tab with basic config UI. Manage button is a
> stub (returns placeholder ephemeral response) — interaction
> handling ships in 5d-2.
