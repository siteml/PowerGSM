# Phase 7-7 — Multiple myrealm accounts (multi-session) `[shipped 2026-06-18]`

## Goal

Let the operator sign in to **several myrealm accounts** and have PowerGSM
keep track of them and use them automatically. Two concrete payoffs:

1. **Import across accounts.** Onboarding (7-6) discovers realms from
   *every* signed-in account, not just one — an owner account plus any
   admin accounts together surface every realm reachable, deduplicated to
   one shared-config group per realm.
2. **Resilient character-name enrichment.** The live consumer that makes
   this matter *today*: lo-myrealm resolves characterID → character name
   by hitting a realm's portal page with a live session. With multiple
   accounts that can reach the same realm, an expired session must hand
   off to another live account automatically, instead of silently losing
   enrichment for that realm.

This is the **multi-session** half of the old decision-2. The
**persisted** session→realm access map and Phase-10 admin routing stay
deferred (see *Deferred*).

---

## Status

`[shipped 2026-06-18]` — designed/confirmed with Site 2026-06-16; built and
verified across all five confirm-gated slices. Per-realm failover proven
live: revoking the serving account makes the next lookup fail over to
another live account and re-home the realm→session cache.

---

## Confirmed decisions

1. **Account key = `myrealm:{accountName}`.** The account name is read
   from the authenticated landing page, which greets `Hello {name}!`
   (confirmed from a live screenshot — e.g. `site_ml`). Same account
   signed in twice → same key → natural dedup. Label shown in UI = the
   account name (the key suffix). No user-typed labels, no "active
   account" selector — discovery/use is automatic across all accounts.

2. **Store primitive lives on the context (Option A).** The key is only
   known *after* login (read from the landing), so saving is a two-step
   flow: capture → read name → save under the derived key. lo-myrealm
   owns the whole flow; the Manager just gains a save verb. **Rejected:**
   having the plugin hand `(key, header)` back for the host to save —
   splits one operation across the plugin/manager boundary for no gain
   (Site: "just asking for trouble").

3. **Admin accounts see the owner's keys.** Empirically settled: `site_ml`
   is only an *admin* of "Site's Private Playground" (not the owner) yet
   7-6 discovery already scraped that realm's CustomerKey + provider keys.
   So every account that can reach realm X scrapes the **same**
   `(CustomerKey, ProviderKey)` → dedup-by-identity is sound across owner
   + admins.

4. **Dedup lives in the plugin.** `DiscoverRecordsAsync` enumerates the
   plugin's accounts, scrapes each, and dedups by `(CustomerKey,
   ProviderKey)` before returning. (Consequence of decision 2's
   "logic in one place" principle — no host-side batch dedup.)

5. **Per-realm failover is in scope, done in-memory in the plugin.** No
   persisted map, no EF migration. lo-myrealm keeps an in-memory
   `realmId → preferred session key` cache; a character lookup uses the
   cached session, and on a redirected/expired/forbidden response walks
   the other live sessions, uses the first that reaches the realm, and
   re-caches. All sessions dead for a realm → no name (same as today's
   single-session-expired behavior, but now only when *every* account is
   down). The "map" is a self-healing cache.

6. **`myrealm:default` left as-is.** The existing single session keeps
   working; it participates as just another account in enumeration
   (label shows "default" unless/until re-read). No migration to a named
   key.

7. **No `ContractsVersion` bump.** Everything is additive:
   - New context methods (`StoreWebSession`, `ListWebSessions`) — plugins
     *call* the context, Manager implements it; old plugins simply don't
     call them.
   - New `IWebPortalDataProvider.AddAccountAsync` — the interface's only
     implementer (lo-myrealm) ships with it, so per the established rule
     ("additive members on an interface whose only consumer ships with
     them don't warrant a bump") this is not breaking.
   - New contracts type `WebSessionSummary` (GSM.Utility).

---

## New surfaces

### Contracts (`GSM.Contracts\IUtilityPlugin.vb`)

- `IUtilityContext.StoreWebSession(sessionKey As String, cookieHeader As String)`
  — gated by `web-capture`. Persists (encrypted) + caches the header
  under the key. The capture→derive→store completion of the existing
  `CaptureWebSessionAsync`.
- `IUtilityContext.ListWebSessions() As IReadOnlyList(Of WebSessionSummary)`
  — gated by `web-capture`. Returns **this plugin's own** stored sessions
  (filtered by `CapturedByPluginId`), so the plugin can iterate accounts
  for both discovery and failover.
- `WebSessionSummary` (new class, GSM.Utility): `SessionKey`,
  `CapturedAtUtc`, `LastUsedUtc?`. Plain DTO — the Manager-side
  `WebSessionInfo` can't cross into contracts.
- `IWebPortalDataProvider.AddAccountAsync(context As IUtilityContext) As Task(Of String)`
  — capture a fresh login, derive the key, store, return the new account
  label (or Nothing on cancel/failure). Distinct from discovery's
  first-capture because it must *force* a new browser capture even when
  sessions already exist.

### Manager (`GSM.Manager\Core\`)

- `WebSessionStore.Store(pluginId As String, sessionKey As String, cookieHeader As String)`
  — public store-under-key (backs `StoreWebSession`). Encrypt + persist +
  cache; stamp `CapturedByPluginId`.
- `WebSessionStore.ListSessions(pluginId As String)` — per-plugin filtered
  overload (or reuse the existing `ListSessions()` + filter in the context
  impl).
- `UtilityPluginHost.AddPortalAccountAsync() As Task(Of String)` — routes
  to the single portal provider's `AddAccountAsync` for the UI. (Mirrors
  `DiscoverAllPortalRecordsAsync`.)
- The context implementation wires `StoreWebSession` / `ListWebSessions`
  through to the store with the calling plugin's id + capability gate.

### lo-myrealm (`GSM.PluginsSource\LoMyrealmPlugin.vb`)

- `ReadAccountNameAsync(cookieHeader)` — GET landing, parse `Hello {name}!`.
- `CaptureAndStoreAccountAsync(context)` — `CaptureWebSessionAsync` → build
  cookie header from `result.Cookies` → `ReadAccountNameAsync` →
  `StoreWebSession("myrealm:" & name, header)` → return name. Used by both
  `AddAccountAsync` and discovery's zero-accounts first-capture.
- `DiscoverRecordsAsync` — enumerate `context.ListWebSessions()` keyed
  `myrealm:*`; for each, scrape (reusing the current per-account walk with
  that account's header via `GetOrCaptureWebSessionAsync(key, …,
  allowPrompt:=False)`); aggregate; dedup by `(CustomerKey, ProviderKey)`.
  Zero accounts + `allowPrompt` → `CaptureAndStoreAccountAsync` then
  scrape. (`requestedKey`, if a concrete key is ever passed, scopes to
  that one account; host passes Nothing = all.)
- Character lookup — add the `realmId → sessionKey` cache + try-and-cache
  failover across live sessions (decision 5). Guard the cache with a lock
  (event dispatch is queued per-plugin, but be safe).

### UI (`GSM.Manager\UI\` — Web Sessions form, 7-5b)

- **Add account** button → `host.AddPortalAccountAsync()` → refresh list.
- The list already shows one row per stored session with validate/revoke;
  multi-account mostly falls out. Show the account-name label (key
  suffix). (Read the form before editing.)

---

## Slices (confirm-gated, in order)

1. **Store + enumerate primitives.** `WebSessionSummary`;
   `IUtilityContext.StoreWebSession` + `ListWebSessions`;
   `WebSessionStore.Store` + per-plugin list; context impl wiring + gates.
   *Test:* compiles; existing single-session behavior unchanged.
2. **Add-account flow.** `IWebPortalDataProvider.AddAccountAsync`;
   lo-myrealm `CaptureAndStoreAccountAsync` + `ReadAccountNameAsync`;
   `UtilityPluginHost.AddPortalAccountAsync`.
   *Test:* drive it from slice 3's button.
3. **Add-account UI.** Button in the Web Sessions form.
   *Test:* sign in a second account; both appear, keyed/labeled by name;
   `myrealm:default` still listed.
4. **Discovery across accounts.** `DiscoverRecordsAsync` enumerate +
   per-account scrape + dedup.
   *Test:* Import shows every realm both accounts reach, one row each
   (owner + admin of the same realm collapse to one).
5. **Per-realm character failover.** The in-memory cache + try-and-cache
   handoff in the character lookup.
   *Test:* with two accounts that both reach a realm, expire/revoke the
   one currently serving it; character names keep resolving via the other.

---

## Deferred (not this phase)

- **Persisted session→realm access map.** The in-memory failover cache
  (decision 5) covers the live need; a durable table only earns its keep
  (and its EF migration) when something must survive restarts or be shown
  authoritatively. Revisit if a consumer appears.
- **Phase 10 — myrealm administration (POSTs).** Still parked. When it
  lands it will reuse the same multi-session enumeration + per-realm
  session selection built here; that's when a persisted map may be worth
  it.

---

## Watch-outs

- **VB case-insensitive shadowing (again).** When honoring per-account
  keys, do not reintroduce a parameter named the same (any casing) as the
  `SessionKey` constant — that exact bug ate a discovery pass in 7-6. The
  parameter is `requestedKey`; keep it that way.
- **Cookie header from `CaptureWebSessionAsync`.** That primitive returns
  `Cookies` (a list), not a header string; lo-myrealm builds
  `name=value; …` itself (the store + `GetOrCaptureWebSessionAsync` deal
  in header strings).
- **Landing-name parse must tolerate absence.** If `Hello {name}!` can't
  be read (markup change, odd account), fall back to a stable but unique
  key (e.g. the completion-URL customer id) rather than colliding accounts
  under one key. Never key two distinct accounts the same.
- **`web-capture` gate.** Both new context methods are gated; lo-myrealm
  already declares the capability.
- **Read before edit.** The Web Sessions form (slice 3/4) and the context
  implementation class have not been re-read this phase — read them before
  editing.

---

## References

- Phase 7-6 (`Phase7_Plan.md`, `[shipped 2026-06-15]`) — discovery,
  `IWebPortalDataProvider`, `PortalImportService`, the per-provider-key
  model this builds on.
- `WebSessionStore` (`GSM.Manager\Core\WebSessionStore.vb`) — keyed store;
  already has `ListSessions()`, `PeekHeader`, `GetOrCaptureAsync`,
  `Invalidate`, `WebSessionInfo`.
- `IUtilityContext` / `WebSessionCaptureResult` / `IWebPortalDataProvider`
  (`GSM.Contracts\IUtilityPlugin.vb`).
- lo-myrealm (`GSM.PluginsSource\LoMyrealmPlugin.vb`) — `SessionKey`
  constant, `DiscoverRecordsAsync`, the character lookup, `GetPageAsync`.
