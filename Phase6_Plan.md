# Phase 6 — Plugin GitHub source + manifest + updates

Design document for fetching, identifying, and updating game plugins
from GitHub sources. Today a plugin is a `.vb` file the user manually
drops into `Plugins\`; Phase 6 turns that into a managed flow — browse
a source, install a plugin, get told when a newer version exists, and
apply it — while keeping the deliberate, never-auto, reversible
discipline the Manager self-update (Phase 5l) established.

**Plan accepted** — the three open decisions are resolved (locked in
the Decisions section). Read this first in a new chat; everything
below assumes a fresh conversation.

---

## Status

**Shipped — Phase 6 complete (all four sub-phases).** Delivered:
the inline `<plugin>` manifest + parser with legacy
`RequiresContracts` back-compat (6-1, incl. manifests on the three
first-party plugins, `version="1.0.0"` baseline, `author="siteml"`);
the `PluginSources` table (EF migration `PluginSources`), official-
source seeding, and the live catalog fetch (6-2); the staging
pipeline with dependency blocking + naming/collision warnings (6-3);
and install / update detection / uninstall with hot-reload (6-4).
One UI evolution beyond the plan: the Plugin Status, Plugin Sources,
and Plugin Updates dialogs were consolidated into a single tabbed
**Tools → Manage Plugins** window (`ManagePluginsForm`), which HOSTS
the three existing forms (TopLevel=False, borderless, docked) rather
than merging their code — each form stays independently
maintainable. Proven end to end against the live official catalog:
install, version-lowered update round-trip, uninstall + reinstall.

---

## Goal

The plugin system today (Phase 6 baseline):

- Plugins are `.vb` files in `Plugins\` (top directory only), each
  Roslyn-compiled into its own assembly inside one shared collectible
  `AssemblyLoadContext`. **Plugins hot-reload** — Tools → Reload
  Plugins unloads the context and recompiles, no Manager restart.
- A `' <RequiresContracts: N>` magic comment is parsed *before* the
  compile so a too-new plugin fails fast with one clear line instead
  of a cascade of "type not defined" errors.
- `GameId` comes from the plugin instance's `.GameId` property;
  `PluginRegistry` keys on it and **skips duplicates**.
- Acquisition is entirely manual: the user finds a `.vb` somewhere,
  copies it in, reloads. There's no notion of *where* a plugin came
  from, *what version* it is, or whether a newer one exists.

Phase 6 adds the missing layer:

1. **Identity + metadata** — an inline manifest header (extending the
   magic-comment convention; no sidecar JSON) so a plugin self-
   describes: id, name, version, author, declared contracts version,
   dependencies.
2. **Sources** — one or more GitHub repos the Manager can browse for
   plugins, with the official source seeded by default.
3. **Acquire + stage** — download a chosen plugin (and its declared
   dependencies) over HTTPS into a staging area, parse + validate,
   without touching the live `Plugins\` folder.
4. **Update + reload** — detect when an installed plugin has a newer
   version upstream, notify (never auto-apply), and apply by moving
   the staged file(s) into `Plugins\` and reloading.

**Contrast with 5l (important):** the Manager can't overwrite its own
running `.exe`, so 5l needed the `apply.cmd` swap + watchdog dance.
Plugins are *data the Manager compiles*, hot-reloadable in-process, so
a plugin apply is just **stage → move into `Plugins\` → `ReloadAll`** —
no process exit, no script, no watchdog. The "separate staging path so
a restart can't half-apply" requirement is satisfied simply by
downloading to a staging folder and moving in only after a full,
verified download.

Out of scope (tracked as future): plugin **signing** (HTTPS transport
integrity only for v1), a curated/rated plugin marketplace, automatic
dependency *discovery* across arbitrary sources, and Node-side plugin
distribution (Node executes; plugins live Manager-side).

---

## Identity + naming model

The disambiguation rule from the roadmap: **official plugins use a
bare id (`factorio`); third-party plugins use a prefixed id
(`author_factorio`)** so an official and a third-party plugin for the
same game are *genuinely different identities* and coexist.

Why this works mechanically: `PluginRegistry` keys on the plugin's
runtime `.GameId` and skips duplicates. Coexistence therefore requires
the two plugins' `.GameId` values to actually differ — so the prefix
isn't something the Manager can bolt on at install time (it can't
rewrite compiled code). It's an **authoring convention the Manager
validates**:

- The **official source** (see Decision 6) is privileged: its plugins
  may use bare ids.
- Any **other source**'s plugins are expected to use an
  `{owner}_{id}` form, where `{owner}` is derived from the *source
  identity* (the GitHub owner), **not** the manifest — so a third
  party can't set `author="PowerGSM"` in their header and masquerade.
- At stage/install time the Manager **validates** the declared id
  against the source: a bare id from a non-official source, or an id
  that collides with an already-installed plugin, is a **warn +
  explicit-confirm** (never a silent shadow).

The Plugin Status / Browse UI labels each plugin's origin (official vs
which third-party source) so the user always knows what they're
running.

---

## Manifest format

Extends the existing `' <RequiresContracts: N>` magic-comment style —
parsed from comment lines before Roslyn ever runs, **no sidecar
JSON**. Proposed shape (one `<plugin>` line + an optional
`<dependencies>` block):

```vb
' <plugin id="factorio" name="Factorio Headless Server" version="1.2.0" author="powergsm" requiresContracts="1">
' <dependencies>
'   <depends id="some_shared_lib" min="0.4.0" />
' </dependencies>
Imports System
...
```

- `<plugin>` is a single logical line of `key="value"` attributes:
  `id` (required), `name`, `version` (semver, required for update
  tracking), `author`, `description`, `requiresContracts` (integer).
  **`author` is pure credit** — free-text, displayed in the UI, never
  used for trust/identity/origin decisions (it's self-declared and
  therefore spoofable; that's exactly why the third-party prefix
  derives from the source owner instead). Official-ness is a property
  of the *source* a plugin is fetched from, not its author — so a
  community-authored plugin accepted into the official repo is
  Official origin while still crediting its real author
  (`author="JaneDoe"`).
- `<dependencies>` is optional; each `<depends id min />` names another
  plugin id and a minimum semver.
- **Back-compat (locked):** the legacy `' <RequiresContracts: N>`
  comment keeps working alongside the new block and is phased out
  slowly — not ripped out now. A file with only the legacy comment
  (and no `<plugin>`) still loads exactly as today, treated as an
  unversioned local plugin (no update tracking, origin = "local").
  The new parser reads `requiresContracts` from either the `<plugin>`
  attribute or the legacy comment. The three first-party files gain a
  full `<plugin>` block and keep their legacy comment for now (see
  Decision 1).
- Parser is regex/line-based and whitespace-tolerant, same spirit as
  `s_RequiresContractsRegex`. Reuses the existing semver type from
  `GitHubReleaseChecker` (`SemanticVersion`).

---

## Decisions

1. **Manifest carries `requiresContracts`; legacy comment still
   honoured — dual-format, phased out slowly.** *(Locked.)* The new
   parser reads the full `<plugin>` block and a `<dependencies>`
   block; a file with only the legacy `' <RequiresContracts: N>` still
   works. We deliberately do NOT break compatibility now — both forms
   are supported and the legacy comment is retired gradually in a
   later phase. The three first-party plugins (`FactorioPlugin.vb`,
   `LastOasisPlugin.vb`, `ConanExilesPlugin.vb`) gain a full `<plugin>`
   block in 6-1 and keep their legacy comment transitionally.

2. **Third-party prefix derives from the source owner, not the
   manifest; collisions warn + confirm.** *(Locked — warn, not
   reject.)* Prevents id spoofing; a bare/colliding id from a
   non-official source is a warning the user can override, never a
   hard block and never a silent shadow.

3. **Sources live in a new `PluginSourceEntity` table.** *(Proposed.)*
   Structured, user-CRUDed, multi-row — matches the Nodes /
   SteamCredentials pattern better than a JSON blob in AppSettings.
   Columns: `SourceId` (PK), `Owner`, `Repo`, `RepoPath` (folder
   within the repo holding `.vb` plugins), `DisplayName`, `IsOfficial`,
   `IsEnabled`, `LastFetchedUtc`. Needs an EF migration
   (`PluginSources`). The official source is seeded on first run and is
   un-deletable (can be disabled).

4. **Catalog discovery = GitHub contents API list + per-file raw
   header fetch.** *(Proposed.)* List `.vb` files in the source's
   `RepoPath` via the contents API (one call, rate-limited — reuse the
   `GitHubReleaseChecker` UA + interval discipline), then fetch each
   file's header bytes from `raw.githubusercontent.com` (no rate
   limit) and parse its manifest. No *required* sidecar index, so a
   third party just drops `.vb` files in a repo. An optional generated
   `plugins-index.json` for the official source is a later
   optimisation, not v1.

5. **Apply = move staged `.vb` into `Plugins\` + `ReloadAll`; no
   restart.** *(Proposed, follows from hot-reload.)* Staging lives in
   `<install>\.plugin-updates\{id}\`. The download writes there in
   full, the manifest + naming + dependencies are validated there, and
   only then is the file moved into `Plugins\` and a reload triggered.
   A mid-download restart leaves a partial file in staging (ignored),
   never in `Plugins\`. Uninstall = remove from `Plugins\` + reload,
   reusing the 5m-2e orphan warning for affected installations.

6. **Official source = `siteml/PowerGSM` at path `GSM.PluginsSource/`.**
   *(Locked.)* The canonical first-party plugins already live and are
   versioned there, so the default catalog is real on day one with zero
   new infrastructure. Since these are the official plugins, keeping
   them in the one repo is fine; revisit a dedicated
   `siteml/PowerGSM-Plugins` repo only if it gets unwieldy.

7. **Dependency resolution is shallow + same-source-plus-official.**
   *(Proposed.)* When staging a plugin, resolve each declared
   `<depends>` against (a) already-installed plugins and (b) the same
   source's catalog and the official source. Missing or too-old deps
   **block** the install with a clear message naming what's needed; no
   transitive cross-source auto-pull in v1. (Most plugins will have no
   deps — the field exists so a future shared-utility plugin can be
   depended on.)

8. **Update detection mirrors 5l's checker.** *(Proposed.)* A
   background pass compares each installed, source-tracked plugin's
   manifest `version` against its source catalog's latest; surfaces a
   count + a **Plugin Updates** view; never auto-applies. Per-plugin
   "latest known" + last-check time persist in AppSettings keys
   (`plugins.*`), same as the Manager update keys. Plugins without a
   `version` or a known source (local drop-ins) are simply not tracked.

---

## Sub-phases

Each is independently testable; order respects dependencies (metadata
→ where-from → acquire → install/update).

### 6-1 — Manifest model + parsing `[shipped]`

- `PluginManifest` DTO (id, name, version, author, description,
  requiresContracts, dependencies[]) + `PluginDependency` (id, min).
- `PluginManifestParser` (Core) — line/regex parse of `<plugin>` +
  `<dependencies>`, whitespace-tolerant, no sidecar. Reuses
  `SemanticVersion`.
- `PluginRegistry` parses the full manifest where it currently parses
  only `RequiresContracts`; stores it per loaded GameId; exposes
  `GetManifest(gameId)`. Legacy-only files still load (origin "local").
- Plugin Status form gains Version / Author / Source columns.
- Add full manifests to the three first-party plugin files.
- **Touch points:** `PluginRegistry.vb`, new
  `PluginManifestParser.vb`, `RemainingForms.vb` (PluginStatusForm),
  the three `GSM.PluginsSource\*.vb`.
- **No network. Test:** existing plugins still load; Plugin Status
  shows version/author; a legacy-only file still loads as "local".

### 6-2 — Source registry `[shipped]`

- `PluginSourceEntity` + config + DbSet + `ApplyConfiguration` + EF
  migration `PluginSources`; seed the official source on first run.
- `PluginCatalogService` (Core) — fetch a source's catalog (contents
  API list + raw header fetch + manifest parse), per-session cache;
  reuses the checker's HttpClient discipline (UA, interval, graceful
  failure). Returns a list of `CatalogEntry { manifest, origin,
  downloadUrl }`.
- **Plugin Sources** management UI (CRUD, modeled on Node setup); the
  official source is un-deletable, only toggle-able.
- **Touch points:** `GsmDbContext.vb` (+ migration), new
  `PluginCatalogService.vb`, new sources form in `RemainingForms.vb`,
  `MainForm.vb` (Tools menu entry).
- **Test:** add the official source, browse its catalog, see the three
  first-party plugins with versions parsed from their headers.

### 6-3 — Download + stage `[shipped]`

- `PluginStageService` (Core), mirroring `UpdateOrchestrator`'s shape:
  given a catalog entry, download its `.vb` (HTTPS/raw) into
  `<install>\.plugin-updates\{id}\`, parse + validate the manifest,
  enforce the naming/prefix rule against the source, resolve declared
  dependencies (block on missing/too-old), and record a staged state.
  Never throws to UI (result object); never touches `Plugins\`.
- Staged-plugins state tracked in an AppSettings key (JSON list) or a
  small column — TBD in 6-3, leaning settings to avoid a second
  migration.
- **Touch points:** new `PluginStageService.vb`, DI registration in
  `ManagerProgram.vb`.
- **Test:** stage a plugin; staging folder populated + validated;
  `Plugins\` untouched; a prefix violation / missing dep blocks with a
  clear message.

### 6-4 — Install / apply + update workflow + reload `[shipped]`

- **Install/apply:** move staged `.vb` (+ staged deps) into `Plugins\`,
  call `PluginRegistry.ReloadAll`, refresh the 5m-2e orphan
  banner/badges. No restart.
- **Uninstall:** remove from `Plugins\` + reload, with the orphan
  warning if installations reference it.
- **Update detection:** background pass (Decision 8) → a status-bar
  indicator + a **Plugin Updates** view listing installed-vs-latest;
  per-plugin Update button runs the 6-3 stage then the 6-4 apply.
  Never auto-applies.
- **Coexistence:** official `factorio` + third-party `owner_factorio`
  load side by side; installations bind to whichever id they were
  created against.
- **Touch points:** `PluginRegistry.vb` (install/uninstall helpers if
  not already), new Plugin Updates view, `MainForm.vb` (indicator +
  menu), `PluginStageService.vb`.
- **Test:** install a plugin from the official source end to end; lower
  a local plugin's manifest `version` to force "update available";
  apply it; install a prefixed third-party plugin alongside an official
  one and confirm both load.

---

## Resolved decisions

All three open questions are settled (June 2026):

1. **Manifest:** dual-format — keep the legacy `RequiresContracts`
   comment working alongside the new `<plugin>` block; phase the legacy
   form out slowly.
2. **Naming enforcement:** warn-and-confirm (not hard-reject).
3. **Official source:** `siteml/PowerGSM` at `GSM.PluginsSource/`.
