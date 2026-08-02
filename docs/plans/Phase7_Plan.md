# Phase 7 — Manager-side utility plugins

Design document for a second plugin kind on the Manager: **utility
plugins** — Roslyn-compiled `.vb` plugins that aren't tied to a game,
don't manage installations or instances, and instead react to what's
happening across the Manager (player events, server state, chat,
instance lifecycle) and contribute capabilities back through a
constrained context. First reference plugin: **lo-myrealm**
(reads authoritative current character names from myrealm for Last
Oasis identity resolution; working title in this doc's early
drafts was "Steam-login-session" — renamed once it was clear the
held session is a myrealm/GPORTAL portal session and the logic is
LO-specific).

**This is a draft seed.** A few decisions need Site's ruling before
code (flagged **[CONFIRM]**); the rest are proposed with rationale
and correctable in a sentence. Read this first in a new chat;
everything below assumes a fresh conversation.

---

## Status

**7-1 through 7-5b shipped (2026-06-12).** Utility-plugin kind,
host + queued dispatch, capabilities/consent/gating + WebView2
capture, the 7-3b static ratchet, the 7-4a event tap, the
`lo-myrealm` reference plugin (7-4b), the shared web-session store
(7-5), and the Web Sessions UI + `IWebSessionValidator` liveness
(7-5b) are all built and confirmed against a live LO realm. **7-6**
(read-only realm onboarding/import) **shipped (2026-06-15)** — generic
discovery → plan → group upsert; the session→realm access map
(decision 2) is deferred until a multi-session topology needs it.
**Phase 10**
(myrealm administration) is `[parked]`. Slots after Phase 6 and
before Phase 9 (per the 0.4.0 release-gate ordering 7 → 9 → 8 in
ROADMAP.md).

**⚠ RELEASE GATE (0.4.0):** `GSM.PluginsSource\TestUtilityPlugin.vb`
is a dev/test artifact placed there ONLY so builds auto-deploy it to
`Plugins\` during Phase 7 — GSM.PluginsSource doubles as the
official plugin catalog, so this file MUST be removed before tagging
0.4.0 or it ships to every user (and its round-2 test variant pops a
Steam login window on every plugin reload).

---

## Baseline (what exists that this builds on)

- **Plugin loading:** `PluginRegistry` compiles each `Plugins\*.vb`
  per-file via Roslyn into one shared collectible ALC; discovers
  types via `GetType(IGamePlugin).IsAssignableFrom(t)`; keys live
  plugins by `GameId`; hot-reloads. Contracts-version negotiation
  compares the manifest/legacy `requiresContracts` against
  `NodeApiContract.ContractsVersion` (currently **1**) — too-new
  fails fast, older loads with a debug note.
- **Phase 6 pipeline:** manifests (`<plugin ...>`), sources,
  staging with consent warnings, install + hot-reload, update
  detection, Manage Plugins window. Utility plugins are just more
  `.vb` files flowing through the same pipeline.
- **Events:** Manager-side player join/leave, server state, and chat
  flow through `InstanceManager`'s log-stream handling and persist/
  notify paths (NotificationService is outbound delivery, not a
  source). 7-2 taps those paths.
- **Identity:** `IdentityResolver` (5g-2d) is the centralised
  identity cache the reference plugin wants to feed.

---

## Honest scoping note: capabilities are consent, not a sandbox

Utility plugins are full-trust compiled code in the Manager process —
Roslyn output can P/Invoke, open sockets, and read files no matter
what we declare. The **declared-capability model is an informed-
consent and convenience-API mechanism, not a security boundary**:

- The manifest declares capabilities; the Phase 6 install consent
  prompt displays them, so the operator approves knowing what the
  plugin says it does.
- The runtime context (`IUtilityContext`) only hands out service
  access for declared capabilities — undeclared access throws a
  descriptive error. That keeps honest plugins honest and makes
  intent auditable in the manifest.
- A malicious plugin can bypass all of it. The real defenses remain
  what Phase 6 built: source provenance (official vs third-party,
  owner-derived prefixes), manual review of `.vb` source (it ships
  as readable source, not a binary), and never-auto-apply.

The plan documents this explicitly so nobody later mistakes the
capability list for sandboxing.

Two enforcement levels were weighed beyond consent: a **static
ratchet** (compile-time reference gating + source audit — adopted
as 7-3b, since the Manager compiles every plugin anyway) and a
**true out-of-process plugin host** (OS-enforced restricted process
+ IPC context — the only real security boundary .NET 8 offers).
The out-of-process host is a major project and is deliberately NOT
planned; Site's working assumption is it will likely never be
needed, recorded here so the door stays visibly open if a real
third-party plugin ecosystem someday demands it.

---

## Decisions

1. **`IUtilityPlugin` is standalone — no `IManagerPlugin` umbrella
   retrofit.** *(Locked.)* Retrofitting `IGamePlugin` /
   `INotificationPlugin` under a common base interface churns
   Contracts for zero functional gain (an empty marker base buys
   nothing the registry can't do with two `IsAssignableFrom`
   checks), and risks subtle source-compat issues for existing
   plugins. If a real shared surface emerges later, introducing the
   umbrella then is cheap. **[CONFIRM]**

2. **ContractsVersion bumps 1 → 2.** New plugin-facing types
   (`IUtilityPlugin`, `UtilityEvent`, `IUtilityContext`, the
   capability enum) land in GSM.Contracts. Additive — game plugins
   declaring `requiresContracts="1"` keep loading (older-than-running
   is fine by design). Utility plugins must declare
   `requiresContracts="2"`, so an old Manager fails them fast with
   the existing one-line message instead of a compile cascade.

3. **Utility plugins REQUIRE a `<plugin>` manifest.** They're new —
   no legacy population to support — so the dual-format leniency
   from Phase 6 doesn't apply. A utility plugin file without a
   manifest id + version is a load error, not a "local plugin".
   Runtime identity: `IUtilityPlugin.PluginId` (the `GameId`
   pattern); mismatch with the manifest id is a load error too.

4. **Event delivery = one method, one DTO.** `Function
   HandleEventAsync(evt As UtilityEvent, ctx As IUtilityContext) As
   Task` with `UtilityEvent.Kind` (PlayerJoin / PlayerLeave /
   ChatMessage / ServerStateChange / InstanceStarted /
   InstanceStopped / InstanceCrashed) plus the common fields
   (instance/installation/node ids, player name + identity fields,
   message, timestamp). One method means adding event kinds later
   never breaks the interface; plugins switch on Kind and ignore the
   rest. Plugins declare interest via a `SubscribedEvents` property
   (list of kinds) so the host doesn't wake every plugin for every
   chat line.

5. **Dispatch is queued + isolated.** A Manager-side
   `UtilityPluginHost` (Core, DI singleton) taps the InstanceManager
   persist/notify paths and enqueues per-plugin (bounded
   `Channel(Of UtilityEvent)`, drop-oldest on overflow with a
   warning). Each plugin drains its own queue on a background task;
   exceptions are caught, logged, and counted (a plugin failing
   repeatedly gets its subscription suspended with a status note —
   visible in Plugin Status). A slow or broken utility plugin can
   never block the Manager's event paths.

6. **v1 capability set (small):** `events`, `identity-read`,
   `identity-write`, `notifications`, `network`, `config`,
   `web-capture`.
   Manifest syntax: a `requires` attribute on the existing tag —
   `' <plugin id="..." ... requires="events,identity-write,network">`.
   `network` is purely informational (consent display); the others
   gate what `IUtilityContext` returns. `web-capture` gates the
   embedded-browser session capture (Decision 7a) and carries the
   most explicit consent line: "this plugin may ask you to log into
   a website in an embedded browser and will receive the resulting
   session cookies." Unknown capability names are a load warning
   (forward-compat), not an error.

7. **`IUtilityContext` surface (v1):** logger; `SendNotificationAsync`
   (routes through NotificationService — `notifications`);
   `ResolveIdentity` / `ContributeIdentity` (IdentityResolver —
   `identity-read`/`-write`); `GetConfigValue`/`SetConfigValue`
   (per-plugin JSON bag in AppSettings — `config`); read-only
   instance/installation listing (always available — it's the same
   data any UI shows); `CaptureWebSessionAsync` (Decision 7a —
   `web-capture`). No raw service-provider escape hatch.

7a. **Embedded-browser session capture is a MANAGER-OWNED context
   service, never plugin-owned UI.** *(Site's design.)* For services
   that authenticate via a web portal (e.g. myrealm's Steam OpenID
   login), the plugin calls
   `ctx.CaptureWebSessionAsync(startUrl, completionUrlPattern,
   cookieDomain)`; the Manager marshals to the UI thread and shows a
   **WebView2** dialog (controlled embedded browser). The user
   performs the REAL login — genuine portal, genuine Steam Guard
   challenge, password-manager autofill all intact; PowerGSM never
   sees credentials and automates nothing brittle. On reaching the
   completion URL the Manager harvests the session cookies via
   `CoreWebView2.CookieManager.GetCookiesAsync` (works for HttpOnly
   cookies — the decisive advantage over any JS-injection approach,
   since session cookies are typically HttpOnly), returns them to
   the plugin, and closes the dialog. The plugin persists them via
   its config/credential storage (DPAPI). Implementation notes:
   WebView2 is a Manager-only dependency (`Microsoft.Web.WebView2`
   NuGet — plugins never reference it, keeping the Roslyn reference
   set unchanged); requires the WebView2 Evergreen Runtime
   (ubiquitous on Win10/11 with modern Edge — the dialog shows a
   graceful "runtime missing" message with the install link rather
   than crashing); the embedded browser uses a dedicated, wipeable
   user-data folder so its state stays contained.

8. **No menu items, panels, or Discord slash-command contributions
   in v1.** *(Proposed scope cut from the old ROADMAP sketch.)* The
   reference plugin needs none of them, building contribution
   surfaces without a consumer is exactly the over-engineering we
   avoid, and each is straightforward to add as a later sub-phase
   when something real wants it. Config editing IS in scope via the
   existing `ConfigFieldDescriptor` + `SchemaFormBuilder` machinery
   (`GetConfigSchema()` on the interface). Note that Decision 7a
   does NOT contradict this: plugins still contribute no UI of
   their own — the browser-capture dialog is a Manager-rendered
   interaction a plugin can *request* through the gated context.
   *(Locked — keep it simple; contribution surfaces wait for real
   demand.)*

9. **UI lives in Manage Plugins.** Utility plugins appear in the
   Status tab with a Kind column (Game / Utility); selecting a
   utility plugin offers **Configure...** (SchemaFormBuilder over
   `GetConfigSchema`). Install/update/uninstall need nothing new —
   Phase 6's pipeline already handles them; the only addition is
   the capability list in the install consent prompt (7-3).

10. **Reference plugin: `SteamSessionPlugin`** — acquires a myrealm
    session via the Decision-7a browser capture (the user performs
    the real Steam OpenID login in the embedded browser; the plugin
    receives and DPAPI-persists the resulting session cookies), then
    watches LO events for unresolved CharacterIds and hydrates
    CharacterId → SteamID via myrealm's API using that session,
    feeding `ContributeIdentity`. On session expiry it raises a
    notification prompting a re-capture. Capabilities: `events,
    identity-read, identity-write, network, config, web-capture`.
    **[input needed]** the myrealm specifics from Site when 7-4
    starts: the post-login URL shape (completion pattern), which
    endpoint exposes the CharacterId → SteamID mapping, and typical
    session lifetime — the plan deliberately doesn't guess them.

---

## Sub-phases

### 7-1 — Contracts + discovery `[shipped]`

- GSM.Contracts: `IUtilityPlugin` (PluginId, DisplayName,
  SubscribedEvents, GetConfigSchema, InitializeAsync(ctx),
  HandleEventAsync(evt, ctx), ShutdownAsync), `UtilityEvent` +
  `UtilityEventKind`, `IUtilityContext`, `UtilityCapability` parsing
  helpers. `ContractsVersion` 1 → 2.
- `PluginRegistry`: discover `IUtilityPlugin` implementers alongside
  game plugins; key by PluginId; enforce manifest-required +
  id-match rules (Decision 3); expose `GetUtilityPlugins()`.
- Plugin Status: Kind column (Game / Utility).
- **Test:** a trivial utility plugin (logs its own Initialize) loads
  and shows as Utility; all game plugins load unchanged; a
  manifest-less utility file errors cleanly.

### 7-2 — Host + event dispatch `[shipped]`

- `UtilityPluginHost` (Core): lifecycle (Initialize on load/reload,
  Shutdown on unload — driven by a new `PluginRegistry.Reloaded`
  event so every reload path is covered without touching call
  sites), per-plugin bounded queue + drain task, failure counting +
  suspension (surfaced as "Suspended" in the Status tab), tapping
  **NotificationEmitter.Emitted** — the same fully-enriched contexts
  NotificationService dispatches — for PlayerJoin / PlayerLeave /
  InstanceStarted / InstanceStopped / InstanceCrashed.
- **Scope note (stamped during 7-2):** ChatMessage and
  ServerStateChange delivery is DEFERRED — those kinds don't flow
  through the emitter today, and tapping the Manager's parsed-event
  path for them is real work with no consumer yet. They land
  alongside whatever identity-field enrichment 7-4's reference
  plugin turns out to need (likely the same tap).
- **Test:** the trivial plugin subscribed to PlayerJoin logs joins
  from a live instance; a deliberately-throwing plugin gets
  suspended without affecting the Manager or other plugins.

### 7-3 — Capabilities + consent + context gating `[shipped]`

Shipping in three testable rounds: **Round 1** (requires parsing,
consent capability lines, gated context services, config store +
Configure... dialog) — shipped; **Round 2** (the WebView2
web-session capture dialog) — shipped (real Steam login + HttpOnly
cookie harvest confirmed); **Round 3** (the 7-3b static ratchet) —
code complete, in test.

**7-3b as built:** (a) *reference-set gating* — a plugin that
declares capabilities but not `network` compiles without the
System.Net.* reference assemblies (`StripNetworkReferences` in
PluginRegistry), so HttpClient/Socket become a compile error naming
the capability; game plugins (no `requires`) keep all references and
are unaffected. (b) *syntax audit* — `PluginSourceAudit.Scan` walks
the parsed tree at stage time for DllImport / Process.Start /
reflection / undeclared-network and surfaces advisory ⓘ lines in the
install + update consent. Known and accepted limits: the audit is
syntactic and conservative — reflection-by-string and obfuscation
are out of scope (only the out-of-process host could catch those,
and that's deliberately unplanned); DllImport/Process.Start/
reflection have no declarable capability in v1, so they're
advisory-only, not gated.

- Manifest `requires` parsing (extends `PluginManifestParser`).
- Phase 6 install/update consent prompts list declared capabilities.
- `UtilityContextImpl` gates by declared capabilities; per-plugin
  config bag; Configure... in the Status tab.
- **Web-session capture service (Decision 7a):** the WebView2
  dialog + `CaptureWebSessionAsync` implementation (UI-thread
  marshalling, completion-URL watch, `CookieManager` harvest,
  runtime-missing fallback message, dedicated user-data folder).
  `Microsoft.Web.WebView2` NuGet added to GSM.Manager only.
- **7-3b — static enforcement ratchet.** Because the Manager itself
  compiles every plugin, two real (if not absolute) gates come
  cheap: (a) **reference-set gating** — a utility plugin that
  doesn't declare `network` is compiled WITHOUT the `System.Net.*`
  reference assemblies, making `HttpClient` a compile error that
  names the missing capability (works for separable assemblies like
  networking; file IO is too entangled with core refs to gate this
  way); and (b) **syntax-tree audit** — scan the parsed source for
  `DllImport`, `Process.Start`, `Reflection`/`Type.GetType`/
  `Assembly.Load` and require matching declared capabilities
  (reflection is itself a declarable, scarily-worded capability),
  surfacing "does X but doesn't declare it" in the install consent.
  Reuses the existing parse + dry-run-compile machinery
  (`PluginCompatibilityChecker` infrastructure). Stops honest
  mistakes and lazy malice; determined reflection-by-string evasion
  remains possible — see the scoping note above.
- **Test:** undeclared capability access throws descriptively;
  undeclared-network plugin fails compile with the capability
  message; the audit flags an undeclared `DllImport`; install
  prompt shows the capability list; config round-trips.

### 7-4 — Event tap + reference plugin `[shipped]`

Split into two, and the order is forced (the plugin can't work
without the tap):

**Key finding (from Site's myrealm screenshots + LO log analysis,
this is what reframed the plugin):** the CharacterId-centric
identity chain Site wanted is ALREADY assembled by existing LO
parse rules — there is no SteamID to "fetch" from myrealm because
the logs already carry it:
  - *Login request* line → CharacterId + PlatformPersona (Steam
    name) + Platform
  - *Character update* line → PlatformUserId (SteamID64) ↔
    CharacterId
  - *Persisting* line → DisplayName (current renamed character
    name) + Platform:PlatformUserId
  - *realm_id* → captured into SessionIdentity
    (`lastoasis:{realm_id}:{tile_id}`) by the LO parser's
    session-identity state machine
So IdentityResolver, fed by these, already links CharacterId ↔
SteamID64 ↔ Steam persona ↔ current display name. **myrealm's
distinct, supplementary value is the AUTHORITATIVE current
character name** (read from the rename-character page's Name
textbox, keyed by realm_id + character_id — both observable from
the log stream), which fills the window BEFORE a Persisting tick
fires and tracks portal renames. The plugin is a name-resolution
helper, NOT a SteamID fetcher (consistent with 5g-3's "backend
APIs are a dead end for identity" finding).

myrealm login flow (confirmed): `https://myrealm.lastoasis.gg/`
→ click Steam → Steam OpenID (`steamcommunity.com/openid/login`,
may prompt for credentials) → back to `myrealm.lastoasis.gg`
(landing shows "Manage my realm"; own vs managed realms redirect
to `/customer/{id}`). Rename-character screen URL carries realm_id
+ character_id; the Name textbox holds the character's CURRENT
name. 7a capture: start `myrealm.lastoasis.gg/Account/SignIn`,
completion pattern `myrealm.lastoasis.gg`, cookie domain
`myrealm.lastoasis.gg`.

#### 7-4a — Utility event tap (no myrealm dependency) `[shipped 2026-06-12]`

The invasive Manager-side plumbing that unblocks the plugin. Today
(7-2) utility events ride `NotificationEmitter.Emitted`, which only
carries `PlayerName` on join/leave and has NO chat/server-state
entry points at all — so a plugin sees neither CharacterId nor
chat. 7-4a must:
  - Surface **CharacterId / Platform / PlatformUserId** onto
    PlayerJoin / PlayerLeave UtilityEvents, and the instance's
    **realm_id** (from SessionIdentity). These originate in the
    Node's EventStore / PlayerSession + the Manager's
    IdentityResolver, NOT in the emitter's token set — so this is
    real plumbing across the Node→Manager player-event path, not an
    emitter tweak.
  - Deliver **ChatMessage** and **ServerStateChange** to utility
    plugins (currently deferred — no emitter methods exist).
  - Decide whether identity enrichment runs through
    IdentityResolver before dispatch (likely yes — gives the plugin
    the resolver's current best answer so it only scrapes genuine
    gaps).
  - **Next session's opening move:** read the Node `EventStore` and
    the Manager's player-event ingestion path (where `/players`
    data becomes Manager-side join/leave) BEFORE editing — this is
    the trickiest plumbing in the phase and wants fresh context.

#### 7-4b — LoMyrealmPlugin `[shipped 2026-06-12]`

(Renamed from "SteamSessionPlugin" — the held session is a
myrealm/GPORTAL portal session, Steam is only the OpenID hop, and
the lookup is LO-specific; generic session handling became 7-5.)

- Ships in `GSM.PluginsSource` as `LoMyrealmPlugin.vb`, id
  `lo-myrealm`. Capabilities: `events, identity-read,
  identity-write, notifications, network, config, web-capture`
  (`notifications` ADDED to the original list — expiry/cancel
  notices need it).
- On PlayerJoin/PlayerLeave of an un-named LO CharacterId: build
  `https://myrealm.lastoasis.gg/realm/{realm_id}/Characters/{character_id}/Rename`
  (realm_id from the event's SessionIdentity, scope = everything
  after the first colon — matches SplitSessionIdentity's identity
  universe), scrape the Name input (attribute-order-tolerant
  regex; worked first try against the live page), and
  `ContributeIdentity` CharacterId → CharacterName.
- **VerifyOnJoin** (added on Site's request, default on):
  already-named characters are re-read on JOIN only to catch
  portal renames; unchanged = no write; never prompts (requires an
  existing session); ≥ 5 min spacing per character, 30 min after
  failures.
- **Manual sign-in trigger**: "Sign in at next plugin reload"
  one-shot config flag — needed because the automatic prompt only
  fires on a genuine naming gap, which never occurs on a realm the
  resolver already fully knows. Clears itself pre-capture;
  fire-and-forget so reload doesn't stall.
- Expiry handled structurally (no-redirect GET; 3xx or served
  sign-in page → invalidate + notify once → next gap re-prompts) —
  the unknown session lifetime is moot.
- **Findings stamped:**
  - The original capture params were WRONG: WebSessionCaptureForm's
    completion check matches ANY navigation including the START
    URL, so a `myrealm.lastoasis.gg` pattern completes instantly on
    the sign-in page with anonymous cookies. Correct params: start
    at root, completion pattern `/customer/`.
  - PluginRegistry's load path reads the LEGACY
    `' <RequiresContracts: N>'` comment, not the inline manifest's
    `requiresContracts` attribute — utility plugins need both lines
    until the registry is taught the manifest (small backlog item).
- **Tested live 2026-06-12:** capture → join → rename detected
  ('site's character' → 'site_tester') in ~500ms → next event
  already carried the new name. Chat/tile-event taps and the
  true-gap auto-prompt path remain spot-check items.

#### 7-5 — Shared web-session store `[shipped 2026-06-12]`

**Motivation (Site, 2026-06-12):** session handling shouldn't live
in a game plugin — multiple future portal plugins would each
reimplement capture/persist/expiry, and 7-4b's cookie header sat
PLAINTEXT in plugin config. A broker *plugin* is the wrong shape
(plugins are isolated; no plugin→plugin provision exists) — the
host already owns the capture dialog, so the host owns sessions.

**Decisions:**
- Two additive `IUtilityContext` members, gated by `web-capture`:
  `GetOrCaptureWebSessionAsync(sessionKey, startUrl,
  completionUrlPattern, cookieDomain, allowPrompt) → cookie header
  or Nothing`, and `InvalidateWebSession(sessionKey)`.
- `WebSessionStore` (Manager Core): in-memory cache + new
  `web_sessions` table, cookie headers encrypted at rest via
  `CredentialService.ProtectString` (DPAPI CurrentUser) — retires
  the plaintext wart. EF migration `AddWebSessions`.
- Session multiplicity = key convention `"{site}:{account}"`,
  plugin-chosen. Plugins sharing a key share the session — that IS
  the cross-plugin provision, with zero plugin coupling. (Optional
  future nicety: label keys from SteamCredential entries. NOT
  auto-login from stored passwords — Steam Guard/2FA is exactly
  what interactive capture avoids.)
- Host-side prompt discipline: concurrent requests for one key
  share one in-flight capture (no double dialogs); after a
  cancelled/failed capture the key is prompt-blocked for the run;
  `InvalidateWebSession` clears the block (the manual re-arm path).
- `ContractsVersion` stays at **2** (NOT bumped). The 7-5 methods
  are additive members on an existing interface, and their only
  consumer (`lo-myrealm`) ships with them — no new-plugin-on-old-
  Manager skew exists to gate, so this is the "routine new member"
  case, not the "new plugin-facing kind" case that justified the
  7-1 bump. `lo-myrealm` declares 2; `testutility` stays at 2.
- LoMyrealmPlugin migrates onto the store as reference consumer:
  drops its own cookie persistence/header building entirely.
- Any-plugin session read-out under `web-capture` is accepted: a
  plugin with that capability could capture its own session anyway
  — same power, consistent with capabilities-as-consent.
- **7-5b — Web Sessions UI + liveness:** "Web Sessions" tab in
  ManagePluginsForm (4th hosted tab): lists key / captured-by /
  captured / last-used — the cookie is never shown or retrievable
  — plus Revoke (store.Invalidate, also the orphan-cleanup path
  when an owning plugin is uninstalled). A "Live" (cache-residency)
  column was built then CUT: lazy-load state reads as "session
  dead" and answers nothing real. Real liveness instead:
  `IWebSessionValidator`, an OPT-IN side-interface on utility
  plugins (additive; adding to IUtilityPlugin itself would
  fail-compile every existing plugin — VB cannot declare default
  interface members — so optional capability = separate interface,
  same pattern as ILogParser/IModManager). Members:
  `CanValidateWebSession(key)` (prefix claim) +
  `ValidateWebSessionAsync(key, header, context)` →
  Valid/Expired/Failed + detail. Host routes via
  `UtilityPluginHost.ValidateSessionAsync` using
  `WebSessionStore.PeekHeader` (read-only, never captures).
  Validators run OUTSIDE the plugin's event queue — thread-safe,
  side-effect-light, classify-only; the UI offers the revoke on
  Expired. lo-myrealm implements it by probing the realm's
  `General/UpdateName` page (exists for the life of the realm, so
  it doesn't depend on any character lookup), and — when no realm
  has been learned from gameplay — self-discovers one from the
  portal landing page (harvest `/customer/{id}` links → first
  `/realm/{id}`), so Validate works right after sign-in. Realm_id
  persisted as `myrealm.realmId` config; "no realm configured
  yet" is reported Valid, not Failed. (Live form fix: the tab is
  hosted borderless in a TabControl, so its client height ≠ the
  form's nominal Size — absolute-positioned bottom controls fell
  off; rebuilt with DOCKED layout.)

**Test:** lo-myrealm works as in 7-4b with cookies now in
`web_sessions` (encrypted) and nothing in plugin config; manual
re-arm still works; two plugins requesting one key produce one
dialog.

### 7-6 — myrealm realm onboarding & import `[shipped 2026-06-15]`

**SHIPPED (2026-06-15) — what was built, and how the open decisions
resolved.** Discovery + import landed end-to-end and confirmed
against Site's live account (3 records across 2 realms): an
**Import…** button on each Shared Resources tab → sign in (reusing
the 7-5 session) → scrape every owned/admin'd realm → a checkbox
New/Update/Unchanged plan → group upsert. The channel is generic
(`IWebPortalDataProvider` in `GSM.Utility`; the Manager-side
`PortalImportService` matches on plugin-declared `MatchFieldKeys`,
never a hard-coded field). Decision resolutions:
- **(1) Enabler — shipped.** `WebSessionCaptureResult.CompletionUrl`
  added; lo-myrealm self-captures inside discovery (re-prompts on a
  stale session).
- **(2) Session→realm access map — DEFERRED.** Discovery already
  harvests the full owned+admin'd set in one landing GET, but the
  persisted map + multi-session routing isn't built: Site's single
  account spans both realms via `myrealm:default`, so there's no
  topology to exercise it. Re-open when a managed realm no single
  stored session can reach appears.
- **(3) Realm store retooling — resolved as PER-PROVIDER-KEY (Site's
  change of heart).** One group per (CustomerKey, ProviderKey) pair
  rather than list-typed fields — zero schema change, models LO
  multi-provider topology faithfully.
- **(4) Where the logic lives — as planned.** Onboarding scrape in
  lo-myrealm (`DiscoverRecordsAsync`), upsert in the Manager
  (`PortalImportService`), UI in `SharedConfigGroupsForm`. Read-only.
- **(5) Two-name-field wrinkle — resolved.** Group DisplayName
  carries the per-provider `"{RealmName} ({UsedBy})"` label (pickers)
  while the History Source column reads the canonical `RealmName`
  field (new non-sensitive `SourceLabelContext.SharedConfigFields`),
  so per-provider entries of one realm read identically in History.

_Original design follows._

**CROSS-CUTTING PRINCIPLE — session is an OPTIONAL provider, never
a dependency (Site, 2026-06-12).** Govern both 7-6 and Phase 10
with this. There are three independently-usable tiers:
  1. **LO game plugin alone** — server management, log parsing,
     and identity resolution (the CharacterId↔SteamID↔persona↔
     display-name chain is ALL log-derived). No myrealm, no
     credentials, no web session. Works today; MUST stay fully
     functional standalone.
  2. **+ lo-myrealm** — adds authoritative character-name lookups
     and (7-6) realm import. Opt-in.
  3. **+ admin (Phase 10)** — adds realm writes. Opt-in.
Every consumer FEEDS OFF the shared session if it's present. How
it behaves when the session is ABSENT differs by consumer and
must not be conflated:
  - **Enrichment (lo-myrealm name lookups)** DEGRADES CLEANLY —
    no session means skip the lookup; log-derived resolution still
    works. The session is optional here.
  - **Admin (Phase 10 realm writes)** has NO fallback — a tile
    burn etc. cannot be done from logs. No session = the admin
    function is simply UNAVAILABLE. This is a genuine hard
    RUNTIME dependency, and the correct "absent" behaviour is to
    disable/grey the admin surface with a clear "needs a myrealm
    session" state — a dependency gate, NOT a degraded fallback.
The distinction that DOES hold across all consumers is COUPLING,
not runtime-optionality: every consumer acquires its session via
the 7-5 share-by-session-key store, never by referencing or
auto-installing lo-myrealm. So admin is loosely COUPLED (store
by key, user-chosen, additive) yet hard-DEPENDENT at runtime
(inert without a session).
Rationale is security/consent: a user who doesn't want to load a
Steam/portal login through PowerGSM simply doesn't opt into the
session-using pieces — base LO management is untouched, enrichment
is skipped, admin is unavailable — and the credential-loading
surface isn't present unless they opt in. This is an argument
AGAINST any "optional dependency" mechanism that would auto-pull
lo-myrealm in — keep it purely additive and user-chosen.

**SCOPE FENCE (Site, 2026-06-12):** 7-6 is READ-ONLY — it scrapes
myrealm to IMPORT realm identity (realm_id, realm name, customer
key, provider keys) into Shared Resources and to build the
session→realm access map. It performs NO writes to myrealm. Realm
ADMINISTRATION (tile burns, settings edits — anything that POSTs)
is explicitly OUT of 7-6 and parked as Phase 10+ (see "Phase 10
— myrealm administration" below). Character ID/name resolution is
already shipped (7-4b + validator); 7-6 only touches it insofar as
onboarding seeds the same session.

**What Site's screenshots established (2026-06-12):**
- Shared Resources → Realms (the 5h shared-config store) already
  holds realm entries with CustomerKey / ProviderKey / RealmName
  (LO plugin shared-config schema), linkable to installations —
  the realm-name autofill target exists today.
- `/customer/{customer_id}` — the capture flow's completion
  landing — contains exactly ONE `/realm/{realm_id}` link, so the
  realm_id is discoverable AT SIGN-IN TIME, no player join needed.
- `/customer/{customer_id}/Providers` lists the account's Customer
  Key and ALL provider keys with "Used by" labels — harvestable,
  and matchable against manually-entered realm config.
- **Account model (corrected by Site):** one account OWNS at most
  one realm but can ADMIN many (limit unknown) — site_ml owns
  Site's World and admins Site's Playground, so ONE session
  actually serves both of Site's realms today. Multi-session
  keying (`"myrealm:{account}"`) is therefore a FALLBACK for
  setups where no single signed-in account spans all managed
  realms — real, but not the common case. Consequence for
  discovery: the authenticated LANDING PAGE (myrealm root)
  enumerates the session's full access set in one GET — "Manage
  my realm" links to the owned `/customer/{id}`, each "Manage
  other realms" card links to an admin'd one (confirmed by Site's
  screenshots: site_ml's landing shows both customer ids). Harvest
  all `/customer/{id}` links there; each customer page then yields
  its `/realm/{realm_id}`.

**Vision:** onboard a realm end-to-end from the Manager: sign in
→ land on the customer page → scrape realm_id + realm name +
customer key + provider keys → create/update the Shared Resources
realm entry → user picks the provider key and links installations.
(Realm administration is NOT part of this — see the scope fence
above; it's Phase 10+.)

**"No realm configured yet" is a first-class onboarding state
(Site, 2026-06-12):** a signed-in customer may have NO realm yet
(the customer page says so rather than linking a /realm/). The
validator already treats this as Valid ("signed in; no realm
configured yet") via the SawCustomers flag. For onboarding, this
is the EXPECTED entry point for a brand-new realm — the flow must
handle "session good, nothing to import yet", offer to re-scan
later (or watch for the realm appearing), and NOT treat the
absence as an error. Likely the realm-CREATE path eventually,
though that's well beyond the scrape slice.

**Decisions to bake before any code:**
1. **Enabler:** `WebSessionCaptureResult` gains a `CompletionUrl`
   property (additive; WebSessionCaptureForm already knows which
   URL matched). That yields customer_id at sign-in for free.
2. **Session→realm access mapping:** model access, not ownership.
   A session's serviceable realms = harvest all `/customer/{id}`
   links from the authenticated landing page (one GET), then each
   customer page's `/realm/{realm_id}` link — the complete map in
   1+N requests, refreshable on demand. Persist the map; route
   each event/probe to a session that can reach its realm. Default
   stays a single `myrealm:default` session; additional keyed
   sessions only when the map shows a managed realm no stored
   session reaches. (The probe-time "no access" signal — 403 vs
   redirect — is now only a nice-to-have for staleness detection,
   no longer blocking.)
3. **Realm store retooling:** multiple provider keys per realm
   with a selectable default (Site). Also note the Customer Key is
   per ACCOUNT, not per realm — today's schema duplicates it across
   realm entries of one account; decide duplicate-and-tolerate vs
   restructure. This is 5h shared-config schema territory (list-
   typed config fields vs first-class entities).
4. **Where the logic lives:** onboarding = Manager UI + lo-myrealm
   scraping methods (TryReadRealmNameAsync + DiscoverRealmIdAsync
   are the first of these, already shipped). All read-only.
5. **Dialog wrinkle:** Edit Realm shows two name fields (entry
   display name + plugin-schema "Realm name") — autofill should
   set both or the schema field should drive the display name.

**Sequencing:** stays `[design]` until 7-4/7-5/7-5b are confirmed
working. First implementation slice when opened: enabler (1) +
realm-name/realm-id/keys onboarding scrape — NOT burns.

**Pulled forward into 7-5b (2026-06-12):** the landing-page realm
discovery (decision 2's harvest mechanism) ships early inside
lo-myrealm's validator — when no realm_id has been learned from
gameplay, it GETs the portal landing, harvests `/customer/{id}`
links, and walks to the first `/realm/{id}`. This was needed so
the Web Sessions "Validate" button works right after sign-in
instead of dead-ending on "no realm seen yet". `DiscoverRealmIdAsync`
+ `GetPageAsync` are the reusable seed of the full 7-6
session→realm access map (which will harvest ALL realms, not stop
at the first).

### Phase 10 — myrealm administration `[parked]`

Deliberately deferred (Site, 2026-06-12) — a large undertaking,
not to be folded into 7-6. Realm ADMINISTRATION from the Manager:
tile "burns", realm settings edits — anything that WRITES to
myrealm.

**Why it's its own phase, not a 7-6 tail:**
- Every action is a POST → anti-forgery/CSRF token extraction per
  page, session-state assumptions, and REAL consequences on
  failure (a mis-fired burn is destructive). Needs consent gating
  and dry-run design that read-only onboarding never does.
- Architecture: admin acquires the shared session via the 7-5
  store-by-key mechanism (see the cross-cutting principle under
  7-6) — loosely COUPLED (no plugin→plugin references, no
  auto-install), but hard-DEPENDENT at runtime: with no session,
  admin is UNAVAILABLE (disabled/grey "needs a myrealm session"),
  not degraded — a tile burn has no log-derived fallback. It
  belongs with LO domain knowledge — the LO game plugin growing a
  utility-side surface, or a sibling utility plugin sharing the
  session by KEY. Site's "optional dependency" idea would be
  manifest optional-dependency semantics the registry understands
  (staging already parses <dependency> for install-time) — but
  per the principle, it must stay user-chosen, never auto-pulling
  the credential surface in.
- Depends on 7-6's session→realm access map + the shared session
  being solid first.

---

## Phase 7 backlog / deferred decisions

**Manifest `requiresContracts` reader + contractless-load policy
(deferred past 0.4.0 critical path).** Two linked items, settled in
principle 2026-06-12, deliberately NOT on 0.4.0's critical path
(purely additive; buys nothing until a contracts bump makes
Manager↔plugin version skew real):
  - **Read side (additive):** teach the registry to ALSO read the
    inline `<plugin … requiresContracts="N">` attribute (attribute
    supersedes the legacy comment when present). Legacy-comment
    reading MUST stay — already-deployed legacy-only plugins depend
    on it.
  - **Contractless-load policy (Site's ruling):** a plugin with NO
    readable version marker currently LOADS as assumed-v1 (proven
    on a v1 build 2026-06-12 — see the deprecation gotcha row in
    `PowerGSM_Reference.md`). New behaviour: **REFUSE by default,
    with a warning**, plus a **toggle for relaxed/legacy rules**
    for anyone who actually needs the old lenient behaviour.
    Rationale (Site): splitting-hairs in isolation, but bulletproof
    default-deny prevents a silent mis-load class, and
    splitting-hairs gaps are exactly what cascade into
    whole-system failures. The toggle keeps the escape hatch
    without making lenient the default.
  - **Plugin-file dual-line convention is independent of both** and
    stays mandatory until the **v1.0 clean break** (deprecation
    horizon: legacy-tag support drops at v1.0 when the program is
    reasonably feature-complete — sub-1.0 is dev-quality, a hard
    break is acceptable there; accelerable only for a deliberate,
    logical reason). Manager keeps READING the comment for
    back-compat; files keep WRITING it for forward-safety against
    old Managers under manual download.

**Release gate (0.4.0):** remove
`GSM.PluginsSource\TestUtilityPlugin.vb` before tagging — a
never-released dev artifact (auto-deploys because GSM.PluginsSource
doubles as the official catalog); deleting it breaks nothing.

---

## Open questions for Site

1. ~~Skip the `IManagerPlugin` umbrella~~ — **resolved: standalone
   `IUtilityPlugin`, no umbrella.**
2. ~~Cut menu/panel/slash-command contributions from v1~~ —
   **resolved: headless v1, config UI stays.**
3. ~~myrealm API/auth details~~ — **resolved: login flow confirmed
   from screenshots; myrealm yields CharacterId → current
   CharacterName via the rename page, not SteamID (see 7-4).**
   ~~Remaining sub-detail for 7-4b only~~ — **resolved 2026-06-12:
   URL format is `/realm/{realm_id}/Characters/{character_id}/Rename`
   (supplied by Site); session lifetime made moot by the defensive
   expiry path.**
