# Phase 5n — Notification scope rework (+ panel ID surfacing) `[shipped]`

*Drafted 2026-06-19.*

## Goal

The `NotificationsForm` scope section (two parallel widgets — an
installations `CheckedListBox` and a dynamically-rebuilt stack of
per-installation instance `CheckedListBox`es) is a mess: the instances
panel is empty on open (reads as broken), and the "leave instances
deselected = all of them" rule is invisible and ambiguous. Replace it
with a clean, legible model:

1. **Richer scope dimensions** — add **Node** and **Instance-set**
   filtering alongside the existing installation/instance filters.
2. **A coherent combination model** — replace the current
   AND-narrowing with **union-of-includes**, which kills the
   "empty = all" ambiguity and expresses every real case crisply.
3. **A legible widget** — a collapsible accordion (the scheduled-
   restart idiom from `EditInstanceForm`) with summary-bearing section
   headers + a live "matches N of M instances" readout, so the
   combined scope is visible at a glance.

Bundled in as an independent slice: **surface InstallationID /
InstanceID / NodeId on their panels** with copy-to-clipboard, because
these IDs are hard to identify from history/logs and a visible backstop
helps when display names aren't user-friendly.

---

## Status

`[shipped]` — all three slices in; final build confirmed 2026-06-21.
- **5n-1 (schema + editor): shipped** 2026-06-20 (build confirmed). The
  `NodeFilterJson` / `InstanceSetFilterJson` columns +
  `NotificationScopeDimensions` migration are applied, and the
  four-dimension accordion editor round-trips all filters with live
  per-section summaries + the match count. Send-time evaluation is
  **still the old AND on the original two filters** until 5n-2 — the new
  Node/Set filters persist but are inert. (As-built UI diverged from the
  original sketch — see *As-built notes* below.)
- **5n-3 (panel ID surfacing): shipped** — `PanelIdLabel` on Node /
  Installation / Instance panels with right-click Copy ID.
- **5n-2 (runtime union + scope fan-out): shipped** 2026-06-21 (build
  confirmed). Both transports moved to union-of-includes; the set-tag
  token is stamped and `{InstanceSetTag}` wired into the template map;
  and installation-level (Update) events are **fanned out** across the
  installation's instances so instance/set-scoped destinations catch
  them. See *As-built notes (5n-2)* below — the runtime diverged from
  the plan on two points (fan-out; template token not free).

Direction confirmed with Site 2026-06-19 (accordion + summary headers +
live count; union-of-includes; sets reuse the existing `InstanceSetTag`).

---

## Numbering rationale

Pre-8, no integer slot free: `6` (plugins) and `7` (utility arc) are
taken, and `Phase8_Plan.md` / `Phase9_Plan.md` already exist as real
designed phases. So this nests as the **next free letter in the live
5-series** (`5m` is the latest; `5j` is taken, `5i` is the only mid
gap and is left alone). `5n` is append-only — it claims no roadmap
space and disturbs no existing ordering. It does **not** gate the
`8 → 9` release ordering ("Phase 8 ships before 0.4.0"); 5n can land
whenever it's convenient.

---

## Confirmed decisions

1. **Union-of-includes scope model** (replaces AND-narrowing). An
   instance is in scope if it matches **any** checked dimension. Each
   dimension contributes includes; an empty dimension contributes
   nothing. **Only** the global all-dimensions-empty state means "all
   instances." Cases this covers cleanly:
   - "all on win-host" → check the node
   - "only the production set" → check the set
   - "win-host plus production" → check both
   - "these three instances" → check them
   The current invisible per-dimension "empty = all instances within"
   rule disappears entirely; emptiness is uniform ("no include from
   here").

2. **Four dimensions: Node, Installation, Instance, Set.**
   - Node: `NodeFilter` (node IDs) — installations are node-bound via
     `InstallationEntity.NodeId`.
   - Installation / Instance: the existing `InstallationFilter` /
     `InstanceFilter`, retained.
   - **Set reuses the existing `InstanceEntity.InstanceSetTag`** — the
     free-form per-instance label already consumed by
     `RuleScope.InstanceSet` and surfaced with autocomplete in
     `EditInstanceForm`. **No new entity, no set-management UI** — the
     notifications form only *consumes* the tag.

3. **Set-tag matching is case-sensitive** (`StringComparison.Ordinal`),
   matching `RuleScope.InstanceSet`'s query-time comparison. The ID
   filters stay `OrdinalIgnoreCase` (GUID IDs). Keep the set its own
   comparer.

4. **Set tag stamped onto `NotificationTokens`** so the send-time
   matcher can see it. `NotificationEmitter.BuildContextAsync` already
   loads the instance; add `tokens.InstanceSetTag = inst.InstanceSetTag`.
   Additive `NotificationTokens.InstanceSetTag` property (GSM.Notification
   — plain token bag, additive, no `ContractsVersion` bump). **Bonus:**
   a usable `{InstanceSetTag}` template token falls out for free.
   *(5n-2 correction: not free — substitution in `DestinationQueue` is
   a hardcoded `ReplaceToken` map, not reflection, so the token was
   wired explicitly. See As-built.)*

5. **Collapsible accordion, not tabs.** One collapsible section per
   dimension (Nodes / Installations / Instances / Sets), each header
   carrying a one-line summary of its own state ("Nodes: 2 selected" /
   "none"), plus a persistent "**Matches N of M instances**" readout.
   Tabs were rejected: they fragment the scope across surfaces you
   can't see at once, which is unusable for a model where the
   combination *is* the point. (Even with union-of-includes, the
   at-a-glance "what's actually in scope" legibility is the whole
   reason the current form fails.)

6. **Migration: two additive JSON columns** on
   `NotificationDestinationEntity` — `NodeFilterJson`,
   `InstanceSetFilterJson`. Forward-only PMC migration
   (`Add-Migration NotificationScopeDimensions`). The existing
   `InstallationFilterJson` / `InstanceFilterJson` columns are retained.

7. **Both transports updated in lockstep.** The webhook matcher
   (`DiscordWebhookPlugin.DestinationCacheEntry.MatchesEvent`) and the
   bot transport's equivalent must move to union together, or the UI
   and actual sends drift. (Bot path not yet re-read — see Watch-outs.)

8. **Cosmetic ID surfacing is an independent slice** (5n-3): UiPanels
   only, no migration, zero coupling to the scope work. Can be pulled
   forward / done first.

### Resolved decision (settled 2026-06-20)

**Back-compat for dual-filter rows: leave existing rows untouched.** A
pre-5n row that set *both* installation and instance filters meant "only
those instances *within* those installations" (AND). Under union it
broadens to "all of those installations **∪** those instances."
**Decision (Site):** no migration-time normalization — existing
destinations keep their stored filters byte-for-byte, and any that
relied on the old AND-narrowing simply have to be re-checked /
reconfigured once 5n-2 flips runtime evaluation to union. Blast radius
is near-zero (destinations are test-only). The semantics flip is called
out in the CHANGELOG when 5n-2 ships.
Rows with at most one of the two filters set (the common case), and
all-empty rows, behave identically under both models — no change.

### Decision added during 5n-2 (2026-06-21)

**Installation-level events fan out across their instances (Option B).**
The plan assumed sets are purely instance-level, so installation-level
events (the three Update events, emitted with `instanceId = Nothing`)
would carry no set tag and simply never match a set/instance scope. In
practice that was counter-intuitive: a destination scoped to "my
production set" got no update notifications for an installation whose
instances are in that set. **Decision (Site):** at emit time an
installation-level event is expanded to carry *every* instance ID and
distinct set tag under the installation; the matcher then matches on
intersection. So a set- or instance-scoped destination catches updates
for any installation hosting a matching instance. Done in the emitter
(one extra query per such event) rather than the matcher caches, so
there's no cache-staleness and both transports get it for free. The
model becomes uniform: "an event carries every scope identifier it
relates to; a filter matches on intersection."

---

## New surfaces

### Data (`GSM.Manager\Data\GsmDbContext.vb`)

- `NotificationDestinationEntity` (~line 436) gains `NodeFilterJson`
  and `InstanceSetFilterJson` (nullable TEXT, JSON string arrays,
  same shape as the existing two filter columns).
- `NotificationDestinationEntityConfig` — no special config needed
  (plain string columns), mirror the existing filter-column treatment.
- New EF migration `NotificationScopeDimensions` via PMC.

### Manager runtime — send-time eval

- `GSM.Manager\Core\NotificationEmitter.vb` →
  `BuildContextAsync`: stamp `tokens.InstanceSetTag = inst.InstanceSetTag`
  in the instance branch (the installation-only branch has no instance,
  so no set tag — correct; sets are instance-level).
- `GSM.Contracts` (GSM.Notification) — additive
  `NotificationTokens.InstanceSetTag As String`.
- `GSM.Manager\Core\DiscordWebhookPlugin.vb`:
  - `DestinationCacheEntry` (~line 411) gains `NodeFilter`,
    `InstanceSetFilter` (`HashSet(Of String)`); parse them in the cache
    build (~line 350) via the existing `ParseStringSet` helper.
  - `MatchesEvent` (~line 422): keep the event-type gate as a separate
    AND. Replace the scope portion with union-of-includes:
    ```
    ' scope gate
    Dim anyFilter = (NodeFilter.Count + InstallationFilter.Count +
                     InstanceFilter.Count + InstanceSetFilter.Count) > 0
    If anyFilter Then
        Dim m = NodeFilter.Contains(tokens.NodeId) OrElse
                InstallationFilter.Contains(tokens.InstallationId) OrElse
                InstanceFilter.Contains(tokens.InstanceId) OrElse
                (InstanceSetFilter.Count > 0 AndAlso
                 Not String.IsNullOrEmpty(tokens.InstanceSetTag) AndAlso
                 InstanceSetFilter.Contains(tokens.InstanceSetTag))  ' Ordinal set
        If Not m Then Return False
    End If
    Return True   ' (after the event-type gate)
    ```
    Guard the token reads against `Nothing` as the current code does.
    `InstanceSetFilter` needs an `Ordinal` comparer; the others stay
    `OrdinalIgnoreCase`.
- **Bot transport** (`GSM.Manager\Core\DiscordBotPlugin.vb`) — apply
  the identical change to its matcher / cache. Confirm first whether
  matching is shared with the webhook plugin or duplicated.

### UI — the scope editor (`GSM.Manager\UI\NotificationsForm.vb`)

- Remove the two-widget block: `_installCheckList` +
  `_instanceSelectorsContainer` (build ~line 218-250), the
  `RebuildInstanceSelectors` rebuild (~line 678), and the
  `OnInstallationChecked` / `OnInstanceItemChecked` handlers (~841 /
  ~718). Verify nothing else references them.
- New collapsible accordion: four sections (Nodes / Installations /
  Instances / Sets), each a collapsible group whose header shows a
  live one-line summary; a persistent "Matches N of M instances"
  label beneath. Reuse the scheduled-restart collapsible pattern from
  `EditInstanceForm`.
- `DestinationEdit` (~line 1137) gains `NodeFilter` +
  `InstanceSetFilter` (`HashSet(Of String)`; Set uses an `Ordinal`
  comparer). Parse in `FromEntity` (the `ParseStringSet` helper exists)
  and serialize in the save path alongside the existing two filters.
- Data the form needs for the sections + match count: nodes
  (`db.Nodes`), installations + instances (already loaded into
  `_allInstallations` with `.Instances`), and distinct non-empty
  `InstanceSetTag` values (`db.Instances.Where(tag<>"").Select(tag)
  .Distinct()` — same source `EditInstanceForm` autocompletes from).
  Match count applies the union predicate over the full instance
  universe.
- Drop the old hint label ("leave all deselected to include every
  instance") — the new empty-state semantics make it obsolete.

### UI — panel ID surfacing (`GSM.Manager\UI\UiPanels.vb`)

Symmetric across all three panels — each has a `_nameLabel` header set
in its load method:
- `NodePanel` (~line 84; name set ~213) → show `NodeId`.
- `InstallationPanel` (~line 397; name set ~732) → show `InstallationId`.
- `InstancePanel` (~line 1632; name set ~2105) → show `InstanceId`.

Add a small dim (gray, ~8pt) ID sub-label positioned between the name
(y≈15) and the status/host line (y≈75), `AutoSize`, with a
`ContextMenuStrip` → "Copy ID" (`Clipboard.SetText(id)`). Labels are
absolute-positioned; insert without disturbing the existing
`Controls.Add` order. Set the label text wherever the entity is already
in hand in the load method.

---

## Slices (confirm-gated, in order)

1. **Migration + editor. ✅ shipped 2026-06-20.** `NodeFilterJson` / `InstanceSetFilterJson`
   columns + PMC migration; `DestinationEdit` gains the two filters
   (parse + serialize); the accordion UI with summary headers + live
   match count, covering all four dimensions (sets included — nearly
   free given the tag already exists).
   *Test:* the editor round-trips all four filters; summary headers and
   the N-of-M count are correct as you check/uncheck. **Send-time
   behavior is still the old AND on the old two filters** — the new
   Node/Set filters are persisted but inert until 5n-2. Flag this in
   the round so it isn't mistaken for a bug.

2. **Runtime union. ⏳ next.** `tokens.InstanceSetTag` stamping in the emitter +
   the additive token; union `MatchesEvent` in `DiscordWebhookPlugin`
   *and* the bot transport, parsing all four filters into their caches.
   Apply the confirmed back-compat decision.
   *Test:* destinations fire per union across node / installation /
   instance / set; all-empty destinations unchanged; a node-only and a
   set-only destination each route correctly; dual-filter rows behave
   per the chosen back-compat policy.

3. **Panel ID surfacing. ✅ shipped.** Dim ID sub-label + copy-to-clipboard on
   `NodePanel` / `InstallationPanel` / `InstancePanel`. Independent of
   1-2; can be done first.
   *Test:* each panel shows the right ID; right-click → Copy puts it on
   the clipboard.

---

## As-built notes (5n-1)

The editor diverged from the original "accordion that scrolls internally;
sections below keep fixed positions" sketch — that produced nested
scrollbars and a fixed-width box that overran the panel. As shipped:

- **One growing, panel-scrolled column.** Scope intro (header + hint +
  match count), the scope box, Events, and Visibility are
  `Dock.Top`-stacked inside a `_lowerHost` panel. `Dock.Top` makes each
  child's **width** track the host automatically (no horizontal scroll at
  any window size); a `_detailsPanel.SizeChanged` handler sizes the host
  to the panel width.
- **Everything grows; the details panel scrolls.** The scope box has no
  inner scrollbar — `RelayoutScope` sets its height to the sum of its
  four section heights, then sizes `_lowerHost` to the full stack, and
  the `AutoScroll` details panel scrolls the whole column. Section
  expand/collapse raises `CollapsibleCheckSection.ExpandedChanged`, which
  re-runs `RelayoutScope`.
- **Lists grow to fit, not scroll.** Each section's `CheckedListBox` is
  sized to `items × itemHeight + buffer` (item height taken from the
  control, floored by a `TextRenderer.MeasureText` fallback, +8 for the
  border) so it shows every row without its own scrollbar.
- **Wheel forwarding.** Because the grown lists never need to scroll
  themselves, each list marks `MouseWheel` handled and forwards the delta
  to the nearest `AutoScroll` ancestor — otherwise the list swallows the
  wheel and the panel won't scroll with the pointer over a section.

## As-built notes (5n-2)

Runtime union shipped 2026-06-21. Deltas from the plan:

- **Both transports carry duplicate matchers** (not shared, as the
  watch-out flagged). `DiscordWebhookPlugin.DestinationCacheEntry` and
  `DiscordBotPlugin.BotDestinationCacheEntry` each got the four-filter
  parse (`ParseStringSet` / `ParseDestStringSet`, set filter `Ordinal`)
  and the union `MatchesEvent`. The bot's separate Discord-panel scope
  matcher (`MatchesPanelScope`) is an unrelated feature and was left
  alone.
- **Scope fan-out (Option B)** — written up as its own decision above.
  The matcher's instance/set legs test `HitAny` over
  `NotificationContext.ScopeInstanceIds` / `ScopeInstanceSetTags` (new
  additive, matching-only collections) instead of a single token;
  node/installation stay single-token. The emitter populates the
  collections: one instance for instance-level events; all instances
  under the installation (a direct `db.Instances.Where(InstallationId=…)`
  query) for installation-level events.
- **`{InstanceSetTag}` was not free.** Substitution in `DestinationQueue`
  is a hardcoded `ReplaceToken` map, so the token was wired explicitly
  after the `{InstanceId}` line. It is **single-valued** and renders
  empty on installation-level update events (no single instance);
  fan-out affects matching only. No default embed field was added, to
  avoid changing default appearance.

The union matcher itself matches the plan's sketch (event-type gate
stays a separate AND; `anyFilter` short-circuit; all-empty = match
everything), with the instance/set legs swapped to `HitAny` for the
fan-out.

The full current-state write-up now lives in `reference/manager.md`
under *Notification routing & scope*, describing the feature end-to-end
(emit → context → cache → match → render) rather than this
slice-by-slice history.

## Deferred (not this phase)

- **Cross-dimension intersection** (e.g. "production-set instances, but
  only on linux nodes"). Union can't express it; a tag absorbs the
  niche case. Not worth the complexity.
- **Instance-set management UI** in the notifications form. Sets are
  authored where they already are (`EditInstanceForm` tag +
  autocomplete); notifications only consume.
- **Instance-set as a first-class many-to-many entity.** The model
  stays one free-form tag per instance.

---

## Watch-outs

- **VB case-insensitive shadowing (the recurring bug).** When adding
  `NodeFilter` / `InstanceSetFilter`, do not introduce a parameter or
  local that collides (any casing) with a class constant/property —
  this exact class of bug ate passes in 7-6 and earlier.
- **Set comparer.** `InstanceSetFilter` must use `StringComparison
  .Ordinal` (parity with `RuleScope.InstanceSet`); the ID filters stay
  `OrdinalIgnoreCase`. Don't let them share a comparer.
- **Bot transport parity.** `DiscordBotPlugin.vb` not re-read this
  phase — read it before 5n-2; confirm whether scope matching is shared
  with the webhook plugin or a separate copy, and update accordingly.
- **`NotificationTokens` is in GSM.Notification.** Adding
  `InstanceSetTag` is additive to a plain token bag; verify no
  serialization/snapshot assumes a fixed shape. No `ContractsVersion`
  bump.
- **Empty-state semantics flip.** The new "empty dimension = no
  include, only global-empty = all" differs from the old per-dimension
  "empty = all within." Call it out in the CHANGELOG when 5n ships.
- **UiPanels labels are absolute-positioned.** Insert the ID label
  between name (y≈15) and status/host (y≈75); `AutoSize` +
  `ContextMenuStrip`; don't disturb the existing `Controls.Add` order
  or anchoring.
- **Read before edit.** `NotificationsForm.vb`, `UiPanels.vb`,
  `DiscordWebhookPlugin.vb`, `DiscordBotPlugin.vb`, and the emitter
  have moved since these line anchors — re-read the relevant regions
  before editing. The removed `RebuildInstanceSelectors` /
  `OnInstallationChecked` / `OnInstanceItemChecked` must have no
  remaining references.

---

## References

- `GSM.Manager\UI\NotificationsForm.vb` — scope build (~218),
  `RebuildInstanceSelectors` (~678), `DestinationEdit` /
  `ParseStringSet` (~1137+).
- `GSM.Manager\Data\GsmDbContext.vb` — `NotificationDestinationEntity`
  (~436, with the existing filter-column doc comments confirming the
  current AND semantics), `InstanceEntity.InstanceSetTag` (~259),
  `InstallationEntity.NodeId` (~98), `NodeEntity` (~26).
- `GSM.Manager\Core\DiscordWebhookPlugin.vb` — routing (~114),
  cache build (~340), `DestinationCacheEntry.MatchesEvent` (~422).
- `GSM.Manager\Core\NotificationEmitter.vb` — `BuildContextAsync`
  token population (NodeId/InstallationId/InstanceId stamped; add
  InstanceSetTag).
- `GSM.Manager\Core\DestinationQueue.vb` — `DiscordEmbedBuilder`
  (`{InstanceSetTag}` template token lands here for free).
- `GSM.Manager\UI\UiPanels.vb` — `NodePanel` (~84),
  `InstallationPanel` (~397), `InstancePanel` (~1632).
- `Phase8_Plan.md` — the phase this precedes (no ordering dependency).
- `Phase7-7_Plan.md` — plan-doc format reference.
