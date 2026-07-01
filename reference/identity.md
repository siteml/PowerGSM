# PowerGSM Reference — Identity & Session Attribution

Part of the PowerGSM reference set (index: [`../PowerGSM_Reference.md`](../PowerGSM_Reference.md)).
How PowerGSM resolves and attributes player identity across the
Manager↔Node boundary: the Manager-side IdentityResolver and its
propagation cascade, Conan-specific identity corrections, and the
connection-binding mechanism that keeps player-leave attribution correct
across reconnects and Manager restarts. Node-side chat dedup lives in
[`node.md`](node.md); the History timeline that consumes these
attributions is in [`manager.md`](manager.md); the connection-binding
VB.NET gotchas are in [`vbnet-gotchas.md`](vbnet-gotchas.md).

---

### Manager-side identity propagation (Phase 5g-2)

Closes the asymmetry from 5g-1 where `ChatMessageEntity`
rows carried full identity (`CharacterId` + `PlatformUserId`
+ `DisplayName`) but `PlayerActivityEntity` rows carried
only the raw parser-verdict name. History timeline rendering
showed the same player under two different names on the
same screen — chosen character name on chat rows, Steam
persona on join/leave rows — because on Last Oasis those
two strings differ by default at character creation for
nearly every player. Not a rename problem; just how LO
identity is structured.

**Architectural choice: write-time snapshot.**
`PlayerActivityEntity` gains three columns — `CharacterId`,
`PlatformUserId`, `DisplayName` — populated at the moment
the join/leave row is persisted via a wire call to the
Node's `/players` endpoint. History rendering coalesces
these against the raw `PlayerName` via `IdentityFormatter`
for display. No Manager-side mirror of the Node's `players`
table, no render-time wire calls per row. Snapshot
semantics across both row kinds matches
`ChatMessageEntity`'s existing approach and is correct in
nearly every case because a character's chosen name is
generally stable across its lifetime; the rare myrealm
admin-rename leaves old rows showing the old name, which
is arguably more honest about what the player was actually
called at that moment than retroactive rewriting would be.

**Schema additions (`GsmDbContext.PlayerActivityEntity`).**
Three new nullable columns: `CharacterId`, `PlatformUserId`,
`DisplayName`. Caps in `PlayerActivityEntityConfig` mirror
`ChatMessageEntityConfig`: 64 chars for the two numeric ID
columns, 100 for `DisplayName`. New non-unique index on
`CharacterId` for future cross-time-range queries per
character — same shape as `ix_chat_character` from 5g-1.
SQLite excludes NULLs from index, so the index doesn't
bloat with pre-migration rows.

**Write-time enrichment
(`InstanceManager.PersistPlayerObservationAsync`).** The
pre-5g-2 sync `PersistPlayerObservation` got split into a
sync entry point that resolves session identity
synchronously up-front, plus an async core that does the
wire call before writing the row. Sync wrapper is what
the SSE log-stream callback calls; the async core runs on
the thread pool so a slow `/players` call can't stall the
SSE reader.

Identity matching tries either `PlatformPersona == playerName`
(the LO common case — the parser delivers the raw login
string) or `DisplayName == playerName` (forward-compatibility
for any future plugin that routes verdicts through the
in-game name). First hit wins. Misses on either surface
leave the identity columns as NULL; the History renderer
falls back to PlayerName via `IdentityFormatter`. The
common miss case is PlayerLeave: the Node's `EventStore`
removes the session from its in-memory dict on the same
log line the Manager is processing, so by the time the
Manager's HTTP request resolves, `/players` no longer
contains the leaving player. PlayerJoin almost always hits.
Documented fallback rather than a bug.

`sessionIdentity` is captured synchronously up-front and
passed in as a parameter so the `ClearPlayerTracking` flush
path (which fires immediately before `StopLogStream` tears
down the parser) doesn't end up with synthetic leave rows
stamped under the `{gameId}:{instanceId}` fallback identity
instead of the actual `realm:tile` identity. The parser-
read happens while the parser is alive; the async wire
call + DB write can run whenever the thread pool schedules
it.

**Shared `IdentityFormatter` helper
(`GSM.Manager.Core/IdentityFormatter.vb`).** New module
with one method: `Format(displayName, platformPersona,
fallback)` returning the first non-empty value. Three
consumers: `HistoryQueryService.LoadTimeline`'s activity-
row assembly, `GsmSlashCommands.BuildPlayersResponse`, and
(implicit) any future caller that needs the same
"DisplayName → PlatformPersona → fallback" decision. The
rule is one line of logic, but inline duplication across
several consumers had already produced subtly different
renderings in 5g-1 testing — having one Format method to
point at when this comes up again keeps the fix in one
place.

The formatter is intentionally a Module, not an injected
service, because the function is pure and stateless.
Forcing every consumer to take a constructor dependency
on a service that wraps a one-line If chain would be
over-engineered.

**Consumer updates.** `HistoryQueryService.LoadTimeline`'s
activity-slice append now populates `TimelineRow.PlayerName`
via `IdentityFormatter.Format(r.DisplayName, Nothing,
r.PlayerName)` and surfaces the snapshotted
`PlatformUserId` + `CharacterId` columns on the row —
previously those were Chat-only. `TimelineRow`'s doc
comments updated to document the new dual-kind
population. `GsmSlashCommands.BuildPlayersResponse`
switched its inline coalesce to
`IdentityFormatter.Format(p.DisplayName, p.PlatformPersona,
"(unknown)")` so the Discord `/players` slash command and
the History window render the same player identically.

**Backfill from `ChatMessages`: dropped.** The original
plan called for a one-shot startup migration that walked
old `PlayerActivity` rows where `CharacterId IS NULL` and
attempted to attribute identity from single-occupant chat
windows on the same session. Discarded during scoping
because on Last Oasis `PlayerActivity.PlayerName` is the
platform persona (Steam handle) while
`ChatMessages.DisplayName` is the chosen character name,
and those differ by default for nearly every player — not
just after admin renames. Name-equality matching across
the two tables would only recover the edge case of players
who happened to pick their Steam handle as their character
name, at non-trivial false-positive risk on busier tiles.
Old rows render via `IdentityFormatter`'s fallback to
PlayerName (unchanged from pre-5g-2 behaviour); new rows
benefit from the snapshot columns going forward.

**Node side: turned out to be already done.** The scoping
conversation discovered that every node-side bullet in the
original 5g-2 plan was already implemented — likely shipped
quietly alongside 5g-1 without a plan update. `players`
+ `instance_state` SQLite tables, `PersistPlayer` upserts,
`PersistInstanceStateSnapshot` upserts, `LoadInstanceState`
+ `RegisterInstance(..., hydrateState:=True)` hydration,
tile-clearing on `EnteringMap`/`LeavingMap`,
`LookupPlayerDisplayName` cached-name lookup. No node-side
code changes needed in 5g-2; the Manager side was the
entire remaining scope.

**Terminology note.** Earlier drafts described the
identity-resolution problem as bridging "the rename gap"
or "renamed characters". That's wrong nomenclature for
Last Oasis: character names are chosen at character
creation and are generally permanent over the character's
lifetime (the CharacterId is stable; the chosen name CAN
change via myrealm admin action but that's a rare edge
case, not a routine player action). The default state —
DisplayName ≠ PlatformPersona — holds for nearly every LO
player from character creation onward, not just after a
rename event. Code comments and doc strings written
during 5g-2 use the corrected terminology; older comments
elsewhere in the tree may still mention renames as the
driver and will be cleaned up opportunistically.

**Migration step.** After source changes land, run
`Add-Migration Phase5g2_PlayerActivity_Identity` in Visual
Studio Package Manager Console, then `Update-Database` to
apply. The migration is purely additive (three new nullable
columns + one index) so existing rows read fine with NULL
identity columns and render via the IdentityFormatter
fallback path.

**Residual gap captured in Backlog as Phase 5g-3.** Short
LO sessions where a player joins, leaves before chatting
AND before the autosave tick, AND no prior session has
cached their (PlatformUserId → DisplayName) mapping —
activity rows for those sessions carry `CharacterId` +
`PlatformUserId` from join/leave events but never resolve
`DisplayName`. Hypothesis: LO log lines emit a richer
actor identity surface (`Player_0_C` /
`OasisPlayerController_0_C` entity names, `{UUID}`-shaped
`ActorGuid` fields) that could bridge the gap via a
transitive identity graph. Investigation requires log
samples; see Backlog.md for the pickup checklist.

---

### Conan-specific identity corrections (Phase 5g-2b)

Live-tested 5g-2 against a Conan Exiles instance and the
History window showed the FLS handle `losno420#72569` on
join/leave rows for a character whose chat rows correctly
rendered as `Gina`. Investigation surfaced two distinct
problems: a Conan-plugin parse-rule labelling error, and a
remaining edge-case gap that the 5g-2 write-time snapshot
doesn't cover.

**Root cause: Conan's `Join succeeded:` carries the FLS
handle, not the character name.** The post-colon token on
`LogNet: Join succeeded: <token>` is structurally a
platform-account identifier — the FLS handle, sometimes
bare (`losno420`) and sometimes with a discriminator
(`losno420#72569`), depending on how Funcom's identity
service has provisioned that account. It is NOT the in-game
character name. The character name only appears later, via
`ConanSandbox: Display: Character ID <n> has name <Name>`
(spawn line, fires ~100-200ms after Join succeeded) and on
every chat line. The Conan plugin's original parse rule
captured this token into `DisplayName`, so the Node's
`PlayerSession.DisplayName` got polluted with the FLS
handle until chat eventually overwrote it. Manager's
write-time snapshot at join caught the bad DisplayName;
at leave time it caught either the bad value or, if chat
had flipped it, found no session match because
`FindExistingSession` was trying to match DisplayName ==
FLS_handle against a session whose DisplayName had become
"Gina".

**Fix 1: Conan parse-rule capture renames.** In
`ConanExilesPlugin.vb`, both the `Join succeeded:` and
`Player disconnected:` rules' capture groups renamed from
`DisplayName` to `PlatformPersona`. Slot semantics now
match Last Oasis: the platform-identity surface goes into
`PlatformPersona` (stable for the session's lifetime),
leaving `DisplayName` free for the actual character name
to land via chat or the Node's `LookupPlayerDisplayName`
cache. The leave-side rename also closes a latent bug:
after chat has flipped DisplayName to the character name,
the leave event's FLS-handle token would no longer match
the session via the DisplayName key, falling through to a
RemoteAddress match (which works, but is fragile);
matching by PlatformPersona is stable across chat updates.

**Fix 2: render-time chat fallback in
`HistoryQueryService.LoadTimeline`.** New helper
`ApplyChatFallbackDisplayNames`. For activity TimelineRows
where the write-time snapshot's `DisplayName` was empty
or equal to the raw `PlayerName`, AND `PlatformUserId` is
populated, the helper looks up the most recent
`ChatMessages.DisplayName` for that (SessionIdentity,
PlatformUserId) pair and overrides `TimelineRow.PlayerName`
with the result. One indexed query per distinct (sid, pid)
pair, leveraging the `IX_chat_pid` index from 5g-1. Handles
the edge case where a player joins on a Node whose
`players` table cache doesn't have them (first-time on
this Node, cross-Node migration, etc.) and the snapshot
comes back with NULL DisplayName.

**Why no Character ID parse rule on Conan.** The
`Character ID <n> has name <Name>` spawn line is tempting
as a `PlayerIdentity` rule — it carries both pieces of the
binding the Node needs. Investigation of EventStore.vb's
`PlayerIdentity` handler showed it'd be a no-op: the
handler has stash paths for `(pid + display, no cid)` and
`(cid + pid, no display)` but not for `(cid + display, no
pid)`. The spawn line's data shape falls in the third
bucket, so `FindExistingSession` misses across all keys
(no session has CharacterId bound yet at that moment) and
the event silently drops. Closing this gap would require
a third stash path on the EventStore side plus a heuristic
to drain it when a session later gains the matching cid
via chat. Deferred to a follow-up; the chat-fallback
Mechanism above covers the common case (returning players
whose chat history has the binding).

**Residual gap for Conan.** First-time-ever players who
join, never chat, and then leave: their join/leave rows
show the FLS handle permanently. The chat-fallback has no
chat to bridge through, and the Character ID line's
spawn-time data shape isn't currently consumable by the
Node's stash machinery. Acceptable trade-off; the binding
lands correctly on the player's first chat in a future
session, and that future session is then a returning-
player scenario the Node's `LookupPlayerDisplayName`
cache handles cleanly.

**Deployment note: living sessions need to reconnect.**
When the plugin hot-reloads, the Manager pushes new parse
rules to the Node via `UpdateParseRulesAsync`, but the
Node's in-memory `PlayerSession` state for currently-
connected players doesn't get re-evaluated. Sessions that
bound under the old rules still have PlatformPersona empty
and DisplayName = FLS_handle. Players need to disconnect
and reconnect once for the new rules to take effect on
their session. Pre-5g-2b History rows showing the FLS
handle stay as-is permanently — no backfill (same
rationale as 5g-2 dropped its backfill: false-positive
risk on player-to-player matching outweighs the value of
recovering edge-case rows).

**Cosmetic follow-up on InstancePanel.** The
"Steam name" column label on the Conan InstancePanel
Overview currently shows whatever lives in `PlatformPersona`
— which post-fix is the FLS handle (not the Steam name).
The label is technically misleading for Conan even though
the data being shown is the most useful platform-identity
string available. Worth either renaming the column to
"Persona" generically or making the column label
plugin-driven; not done in 5g-2b to keep scope focused.

---

### Player leave attribution across Manager reconnect/restart (connection bindings)

There are two independent player-tracking systems, and the player's
IP:Port lives in a different place in each:

1. **Node `EventStore`** — the authoritative live list behind
   `/api/instances/{id}/players`. Tracks each session by identity AND
   `RemoteAddress`, and on a close line resolves the player
   (CharacterId → PlatformUserId → RemoteAddress → DisplayName →
   PlatformPersona) and removes them. This is why the Overview tab's
   player list catches an idle-kick even when History doesn't.
   `PlayerSession.RemoteAddress` is populated here and IS the value
   shown in the Overview "IP Address" column.

2. **Manager `LastOasisLogParser`** — re-parses the same log lines the
   Node streams up and keeps its own `RemoteAddr → name` table
   (`_connectionsByAddr`), bound at `Join succeeded`. **This is the
   path that feeds History and Discord notifications** (via
   `HandlePlayerJoin` / `HandlePlayerLeave` →
   `PersistPlayerObservation` and the notification emitter). LO close
   lines (`UNetConnection::Close`, `UChannel::Close ChIndex==0`) carry
   only `RemoteAddr`, so this table is the ONLY thing that turns a
   close into a *named* leave.

**Why it broke.** The parser is recreated on every log-stream
reconnect, and a full Manager restart starts a fresh process — either
way `_connectionsByAddr` was empty for any player who joined *before*
that event. A clean quit still survives (its `UNetConnection::Close`
falls through to InstanceManager's "exactly one player online"
nameless-leave heuristic), but a `UChannel::Close`-only idle-kick/
timeout has no `UNetConnection::Close`, so the parser no-matched it and
dropped the leave outright. The drop then **cascaded** through the
name-keyed `_activePlayers` dedup bucket: the stale name stayed in the
bucket, so the player's *next* reconnect Join was suppressed as a
duplicate (`bucket.Add` returns False → no row, no notification), and
that reconnect's close fired a leave that closed the *prior* still-open
session. Net effect: several real log sessions collapsed into one
mis-paired History entry (observed 2026-05-30: three log sessions, two
History/Discord sessions).

**The fix (three parts).**

- `IConnectionBindingAware` (new opt-in contract interface, GSM.Plugin)
  — the LO parser exposes its `_connectionsByAddr` through it; the
  Manager owns one dictionary per instance (`_connectionBindings`) and
  injects the SAME instance on every parser (re)creation in
  `StartLogStream`, so bindings survive in-process reconnects. Cleared
  only in `StopLogStream` (real stop), never on reconnect. Conan /
  Factorio don't implement it and are untouched.

- **Rehydrate from `/players` on resync.**
  `ResyncActivePlayersFromNodeAsync` now seeds
  `_connectionBindings(instanceId)(sess.RemoteAddress) =
  sess.PlatformPersona` for every online session before the bucket
  sync. This covers the *restart* case (the in-memory store is empty on
  a fresh process, but the Node's `/players` still carries every online
  player's `RemoteAddress`). The address format matches on both sides
  (`IP:Port`, from the same `NotifyAcceptedConnection` line). The
  parser's first post-restart close then resolves, fires the leave, and
  clears the bucket so the next Join registers.

- **Synthesis dedup is no longer cross-clock.** The join-synthesis
  skip-condition in the same method dropped its
  `mostRecent.TimestampUtc >= joinedUtc` clause. The live join row is
  stamped at the Manager's `DateTime.UtcNow`; `joinedUtc` is the Node's
  clock. A Manager clock running a few seconds behind the Node made the
  `>=` fail and synthesized a *duplicate* join (the blank-then-named
  pair). It now skips whenever the most-recent row is an open join,
  regardless of timestamp.

**Facts worth remembering.**

- `PlayerSession.RemoteAddress` IS carried on `/players` and IS
  populated by the Node. History rows and notifications, by contrast,
  are keyed purely on identity (persona/character) — the IP:Port never
  lands in a History row or a notification payload; it exists only as
  the parser's ephemeral correlation key. (So no Node rebuild was
  needed for this fix; it consumes an existing wire field.)
- The Manager clock can run ~3 seconds behind the Node/game-server
  clock. Never dedup Manager-stamped rows against Node-stamped
  timestamps with a strict `>=`; dedup on *state* (does an open join
  already exist?) instead.

**Implemented (the `/players`-diff reconcile, resync "Pass 1.7"):** a
player who leaves *entirely while the Manager is offline* produces no
close line for the parser to process, and isn't in `/players` to
rehydrate. The resync now diffs the Node's authoritative `/players`
against the Manager's open joins: any player whose most-recent activity
row on an instance is a Join but who is absent from that instance's
`/players` is synthesised a Leave via `PersistPlayerObservation`
(persist-only — no Discord ping, since the departure is in the past;
mirrors the terminal-state synthetic-leave policy). The synthesised
Leave is stamped at *detection* time (`DateTime.UtcNow` when the resync
runs), not the true departure time — the reconcile can't know when the
player actually left (the premise is that no close line was observed),
and the Node retains no departure time for an already-gone player. With
a brief outage the stamp reads close to the real departure; after a
long one it reads at reopen. Two guards:

- **Scoped by InstanceId, not SessionIdentity.** LO's SessionIdentity
  is realm-wide and spans every tile/instance on the realm, so diffing
  realm-scoped open joins against ONE instance's `/players` would
  falsely "leave" a player online on a sibling tile. The open-join
  query filters on `PlayerActivity.InstanceId`.
- **Gated on Node uptime ≥ 5 min.** Right after a *Node* restart the
  Node's `/players` under-reports still-connected players (it resumes
  log tailing from a saved byte offset and never replays old Join
  lines), so a diff in that window would synthesise false leaves. The
  reconcile fetches `NodeStatusResponse.UptimeSeconds` and skips when
  the Node hasn't been up long enough.

**Residual edge (accepted):** a player who stays connected but totally
silent across a Node restart never re-appears in `/players` and would
eventually be reconciled as left once Node uptime passes the gate — the
same class as the existing node-restart player-state gap. A
`/players`-diff fundamentally can't tell "silently still connected
after a node restart" from "gone", because the Node itself doesn't know
about them. Rare; documented rather than solved.
