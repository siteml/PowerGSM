# Phase 4c — Configuration UI, Save Management, and Map Generation

Design document for the next major feature push. Read this first in the
new chat; everything below assumes the conversation is starting fresh.

---

## Status (closeout)

All six design decisions and all build phases below are landed and
tested against a live Factorio instance. Two notable deltas from the
original plan:

- **Phase numbering shuffled during implementation.** The plan had
  `4c-3 = server config editing` and `4c-5 = map generation`; the
  shipped code reversed this (`4c-3 = generic file generation`,
  `4c-4 = server-settings editor`). Calling out here so a reader
  comparing this document to the code doesn't get confused.
- **Phase 4c-2 scope was narrowed** — the planned full
  `StructuredConfigSchema` (sections, nested groups, `VisibleWhen`
  expressions) became a single new field type (`ManagedFilePicker`)
  on the existing flat `ConfigFieldDescriptor`. The flat schema
  turned out to be enough for everything 4c needed; the richer
  structured form is a v2 follow-on if a future plugin needs it.

All the gory detail lives in PowerGSM_Reference.md — the
"Phase 4c-1" through "Phase 4c-4" sections under POST-PHASE
ADDITIONS. This document is the original design rationale; the
Reference doc is the canonical "what shipped."

| Item | Status |
|---|---|
| D1 — file-as-truth for runtime config | Done (Phase 4c-4) |
| D2 — install-scoped saves & runtime config | Done (Phase 4c-1) |
| D3 — map gen as sibling tab to Saves | Done (Phase 4c-3) |
| D4 — visibility checkboxes + auth section | Done (Phase 4c-4) — simpler form than the conditional auth radio originally specced; see notes below |
| D5 — hardcoded map-gen presets | Done (Phase 4c-3) — 7 presets shipped (Default, Death World, Rail World, Ribbon World, Rich Resources, Lakes, Island) |
| D6 — stream uploads, no cap | Done (Phase 4c-1) |
| Phase 4c-1 — node-side file ops | Closed (per plan) |
| Phase 4c-2 — saves UI + ManagedFilePicker | Closed (narrower scope than original "structured config schema" plan — ManagedFilePicker field type only, no nested sections / VisibleWhen; see notes) |
| Phase 4c-3 — generic file generation | Closed (originally specced as Phase 4c-5 map-gen; generalised to `IFileGenerationProvider` — 7 Factorio presets shipped: Default, Death World, Rail World, Ribbon World, Rich Resources, Lakes, Island) |
| Phase 4c-4 — server-settings.json editor | Closed (originally specced as Phase 4c-3 "server config editing" in plan; renumbered during implementation) |
| Phase 4c-5 — (was: map generation as standalone phase) | Subsumed by Phase 4c-3 |
| Phase 4c-6 — polish & docs | Closed (this document + reference doc + CHANGELOG) |

### Differences from the original spec

Four places where the implementation diverged from what's written
below, kept here so a future reader of this plan understands why the
shipped code doesn't match line-for-line:

- **Phase numbering reshuffled.** Plan had `4c-3 = server config
  editing`, `4c-4 = save management UI`, `4c-5 = map generation`.
  Implementation flipped this: `4c-2 = save management UI` (folded
  into the structured-schema phase since they were both about saves
  navigation), `4c-3 = map generation` (renamed to generic file
  generation — see below), `4c-4 = server-settings editor`. Why:
  during 4c-2 it became clear save management was the natural
  exercise of the file-ops layer the schema work needed, and rolling
  them into one phase let the SaveFile field's `ManagedFilePicker`
  drop straight into Factorio's existing instance config schema.
  Server-settings ended up later because file-as-truth (D1) needed
  the upload/download endpoints from 4c-1 to be solid first.

- **Phase 4c-2 scope was narrowed.** Originally specced as a full
  `StructuredConfigSchema` with sections, nested groups,
  `VisibleWhen` expressions, and `StringList`/`IntegerList` field
  types. What actually shipped is a single new field type
  (`ManagedFilePicker`) on the existing flat `ConfigFieldDescriptor`
  schema, plus a 3-arg overload on `SchemaFormBuilder.Build` that
  accepts a file-list provider. The flat schema turned out to be
  enough for the use cases that landed (Factorio save selection,
  server-settings editing), and section headers / nested groups got
  deferred to v2 follow-ons. If a future plugin needs the richer
  structured form, the existing `ConfigFieldDescriptor` schema can be
  extended in place rather than introducing a parallel system.

- **D4's conditional auth radio became plain checkboxes.** Spec
  called for an "Auth method: Token / Account" radio that hid
  unrelated fields when toggled. Shipped form is flat — username,
  token, and game_password are all visible at once. Reasoning: the
  conditional logic added `VisibleWhen` expression evaluation that
  was only used in this one place, and Factorio's docs encourage
  filling in token-based auth with no harm in leaving game_password
  blank, so the tri-state hiding wasn't worth the complexity. Users
  who want guidance get it via the `[Auth]` description prefixes on
  each field.

- **`IMapGenerationProvider` was generalised to
  `IFileGenerationProvider`.** During Phase 4c-3 implementation it
  became clear the contract shape (schema-driven + step-list output)
  applies to any one-off file-producing operation, not just maps.
  Renamed before shipping; the wire-level DTOs (`GenerateMapRequest`/
  `GenerateMapResponse`, endpoint URL `/api/instances/{id}/generate-map`)
  kept their original names for back-compat with already-deployed
  nodes. A NAMING NOTE comment in `NodeApiContract.vb` explains.

### v2 follow-ons (deferred, not blocking 4c closeout)

- **SchemaFormBuilder section-header support.** The 4c-4 server-
  settings editor has 18 flat fields with `[Section]` description
  prefixes as a workaround. A proper section-break field type
  would let plugins group fields visually without the prefix hack.
  ~30 lines of code in SchemaFormBuilder, no contract change beyond
  a new `ConfigFieldType.SectionHeader` enum value.

- **Richer schema-driven map presets.** Today the Factorio plugin
  hardcodes 7 presets as static JSON blobs. A v2 path would expose
  every map-gen-settings parameter (terrain segmentation, water
  coverage, per-resource frequency/size/richness) as form fields
  under the existing `IFileGenerationProvider.GetGenerationSchema`,
  letting users build custom presets without dropping into JSON.
  Hardcoded presets remain valuable as starting points; this
  augments rather than replaces them.

- **Map exchange string import** (D5 v2 note). Factorio's in-game
  `/c helpers.parse_map_exchange_string("...")` produces JSON for
  any exchange string a user has obtained from elsewhere. We could
  either run this conversion via Factorio in headless mode at the
  node, or implement the parser directly. Easy power-user feature.

- **Per-instance config scope via `{InstanceId}` token** (D2 note).
  The token substitution exists in the contract for both
  `ManagedDirectory.RelativePath` and `InstanceFileEditor.RelativePath`
  but no shipped plugin uses it (Factorio is single-instance-per-
  installation by design). Reserved for a future game whose layout
  is multi-instance.

- **Factorio scenario support.** Considered during the preset round.
  Scenarios use different CLI semantics (`--start-server-load-
  scenario` at runtime, not `--create`) and the available docs on
  arguments like `--map2scenario` are unclear, so deferred.

---

## Goal

Let users configure game servers, manage save files, and generate new
maps without ever opening a config file by hand. The features should
generalise — Factorio is the driving case but the contract additions
should be plugin-shaped so future games (Minecraft `server.properties`,
Last Oasis `Game.ini`, etc.) can opt in.

User-facing capabilities being added:

1. **Server config editing** — friendly form for `server-settings.json`
   (visibility, password/token, max players, autosave, allow_commands,
   etc.), persisting back to the file on the node.
2. **Save management** — list/upload/download/delete saves on the node;
   pick which save the instance starts with; multiple saves per
   installation.
3. **Map generation** — generate a new save from presets (death world,
   rail world, standard, etc.) with optional fine-tuning, then make
   that save available to start.

---

## Honest assessment of the current infrastructure

### What's already there and reusable

- `IGamePlugin.GetInstanceConfigSchema` returns a flat list of
  `ConfigFieldDescriptor` and `SchemaFormBuilder` renders it. Works
  for primitive scalars; fine as the foundation we extend, not
  replace.
- Install-step runner on the node executes a list of typed
  `InstallStep` subclasses. `RunProcessStep` already lets a plugin
  ask the node to run an arbitrary command and wait for it. Map
  generation (`factorio --create`) fits this perfectly — generation
  is just a one-off operation with a different step list.
- `InstallationEntity.ConfigJson` and `InstanceEntity.ConfigJson`
  persist plugin-declared key-value configs in the manager DB.
- Plugin opt-in interface pattern is already established
  (`IVersionAwarePlugin`, `IInstallationNoticeProvider`,
  `ILaunchOptionsProvider`). New capabilities slot in as more of
  these — plugins that don't implement them, don't get the feature,
  no breakage.
- Install operation lifecycle (`InstallationOperationState`,
  `Queued/Downloading/Configuring/Validating/Completed/Failed`) plus
  progress polling and prompt response is the right shape for any
  long-running node operation, not just installs.

### What's missing and needs to be built

1. **Nested/grouped schema.** `server-settings.json` has
   `visibility: { public, lan, steam }`, lists
   (`tags: []`, `admins: []`), and 30+ fields organised into
   logical groups. The current flat schema can't represent this
   without becoming a wall of unrelated fields.
2. **File-backed config.** Today a config exists in the DB. For
   `server-settings.json` the file ON THE NODE is the source of
   truth at runtime — the manager needs to read it (so what the
   user sees matches what the server uses) and write it back. No
   such read/write plumbing exists today.
3. **Manager↔node file transfer.** No upload/download endpoints.
   Save management needs both directions.
4. **Node-side file operations on demand.** No "list files in this
   directory" or "delete this file" endpoint scoped to an instance.
5. **One-off plugin operations.** Map generation is a non-install
   plugin-defined operation. The install runner could be
   generalised, or we add a parallel "run operation" endpoint that
   takes a step list. Either is small.
6. **Tabbed or multi-pane instance UI.** Today "edit instance" is
   one form. We need a host that can show several panes —
   General, Server Settings, Saves, Map Generation — with each pane
   appearing only if the plugin opts in.

Nothing on this list is architecturally novel; each is an
extension of an existing pattern.

---

## Resolved design decisions

These were settled in the planning conversation. Reasoning kept so
the next chat doesn't re-litigate them.

### D1. Source of truth for runtime config files (e.g. `server-settings.json`)

**Hybrid: file is truth at view/edit time; manager writes the file
directly on save; manager does NOT regenerate it on start.**

The manager fetches the file from the node fresh whenever the user
opens the config form, deserialises into the form, writes back on
save via the file-upload endpoint. The DB doesn't store this
file's content. Out-of-band edits (operator SSH'd in and edited
directly) survive. Offline editing of runtime configs isn't
supported — coherent thing to not support.

### D2. Scope of saves and runtime config

**Installation-scoped, not instance-scoped.**

Saves and server-settings.json live in the install dir
(`<install>/saves/`, `<install>/server-settings.json`) and are
shared across all instances of that installation. Matches
Factorio's filesystem layout and the framework's
`MaxInstancesPerInstallation = 1` pattern for that game.

**Instance-scoped configs are reachable on opt-in.** A future
plugin that supports many concurrent instances per installation
can return paths containing a `{InstanceId}` token in its
`IServerConfigProvider` declaration, and the manager substitutes
before calling the file ops endpoint. Path token substitution
already exists for `LogFilePaths` so this is a small extension.

### D3. Where map generation lives in the UI

**Non-modal sibling tab to Saves, opened on demand.**

The Saves tab has a *Generate New* button. Clicking it opens a
*Generate Map* tab alongside the existing instance tabs. User
configures (preset, optional fine-tune, save name) and hits
Generate. The generation operation runs with progress shown in
the Generate Map tab. Other tabs remain navigable while it runs
— the user can monitor the running instance's logs, edit
unrelated config, etc.

When the operation completes, the Generate Map tab shows a
success state with a "Show in saves" link that switches back to
the Saves tab with the new save selected. User can also just
close the Generate Map tab; the new save is already in the
list.

This preserves the "saves is where save-related things happen"
discoverability without the modal-blocks-everything penalty.

### D4. Surfacing visibility and auth in the server-settings form

**Three independent visibility checkboxes (raw), conditional auth
section.**

The visibility flags map 1:1 to the JSON object — they're
genuinely three independent toggles, not one tri-state, and
pretending otherwise misleads the user. Form shows:

- ☐ Public matchmaker (sets `visibility.public`)
- ☐ Broadcast on LAN (sets `visibility.lan`)
- ☐ Steam friends list (sets `visibility.steam`)

The only synthesis is in the auth section, which only appears
when "Public matchmaker" is checked. (Steam-only and LAN-only
servers don't need Factorio account credentials.) Within the
auth section:

- Auth method: ⦿ Factorio token (recommended) ◯ Factorio account
- Token field shown for the token option; username + password
  fields shown for the account option.

This collapses what's actually confusing — `username`/`password`/
`token` are alternatives, not concurrent — into a single explicit
choice. The 1:1 visibility part stays 1:1 because it's not
confusing in the first place.

### D5. Map gen presets

**Hardcoded in the plugin for v1, with the drift risk documented.**

Plugin ships preset `map-gen-settings.json` blobs as VB string
constants. Initial set: Default, Death World, Rail World, Ribbon
World — the four most-used in-game presets. Comment in plugin
source notes "as of Factorio 2.x; may drift if Factorio adds
or revises presets in the future." Cheap to update; doesn't
warrant the dump-data extraction complexity.

**v2 follow-on (not v1 scope):** add an "Import from map exchange
string" path. Factorio's in-game `/c
helpers.parse_map_exchange_string("...")` produces JSON for any
exchange string a user has obtained from elsewhere. We could
either run this conversion via Factorio in headless mode at the
node, or implement the parser directly. Easy power-user feature
for later; doesn't change the v1 architecture.

### D6. Save upload size

**Stream uploads, no cap.**

User has confirmed real Factorio saves run 100+ MB. New endpoint
streams the request body directly to disk via
`Request.Body.CopyToAsync` rather than buffering. ASP.NET Core
allows arbitrary body sizes once the form-options size limit is
disabled for that endpoint. Manager uses
`HttpClient.PostAsync(StreamContent(fileStream))` for matching
streamed sends.

---

## Proposed phasing

Numbered phases below build on each other. Each ends at a
shippable state with the previous functionality intact.

### Phase 4c-1: Node-side file operations

Foundation for everything else. Adds file CRUD endpoints scoped
to an instance's working directory, with plugin-declared
whitelisted subpaths (saves/, config/, mods/, etc.) so plugins
decide what's manageable.

**Contract additions:**
```vb
Public Class FileEntry
    Public Property RelativePath As String
    Public Property SizeBytes As Long
    Public Property ModifiedUtc As DateTime
End Class
```

**New plugin interface** (opt-in):
```vb
Public Interface IManagedDirectoriesProvider
    Function GetManagedDirectories(config As InstanceConfig) _
        As IReadOnlyList(Of ManagedDirectory)
End Interface

Public Class ManagedDirectory
    Public Property RelativePath As String      ' "saves"
    Public Property DisplayName As String       ' "Saves"
    Public Property Permissions As DirPermissions  ' Read|Write|Delete
    Public Property AllowedExtensions As List(Of String)  ' ".zip"
End Class
```

**New node endpoints** (all scoped to instance + whitelist):
- `GET /api/instances/{id}/files?path=saves` → list
- `GET /api/instances/{id}/files/download?path=saves/foo.zip` → stream
- `POST /api/instances/{id}/files/upload?path=saves/foo.zip` → streamed body to disk
- `DELETE /api/instances/{id}/files?path=saves/foo.zip` → delete

Path validation rejects `..` traversal, requires the resolved
path to be under one of the plugin's declared
`ManagedDirectory.RelativePath` entries, and enforces
`AllowedExtensions`.

**Manager-side wrapper** in `NodeHttpClient`.

**Acceptance:** unit-test from a manager-side test page that all
four ops work against a Factorio installation's saves directory.
No UI for end-user yet. Last Oasis untouched.

### Phase 4c-2: Structured config schema

Introduces a richer schema type alongside the existing
`ConfigFieldDescriptor`. Sections, nested groups, lists of
primitives, conditional visibility (for D4's auth section).
Form builder grows a tabbed/sectioned variant.

**Contract additions:**
```vb
Public Class ConfigSection
    Public Property Key As String
    Public Property DisplayName As String
    Public Property Description As String
    Public Property Fields As List(Of ConfigFieldDescriptor)
    Public Property Sections As List(Of ConfigSection)  ' nested
    Public Property VisibleWhen As String  ' optional expression
End Class

Public Class StructuredConfigSchema
    Public Property RootSections As List(Of ConfigSection)
End Class
```

`VisibleWhen` is a small expression evaluated against current
form state — covers cases like "show auth section when
visibility.public is true". Keep the expression language
deliberately tiny (single-field equality / boolean truthiness)
so it stays serialisable and predictable.

New `ConfigFieldType` values: `StringList`, `IntegerList`.

**Manager-side renderer:** `StructuredFormBuilder` that walks the
schema tree, producing a form with collapsible sections or a
TabControl for the top level. Renders existing
`ConfigFieldDescriptor` types unchanged. Re-evaluates
`VisibleWhen` on every field change.

**Acceptance:** can render a hand-built test schema with two
top-level sections, a nested group, a string list, and a
conditionally-visible section. No plugin uses it yet.

### Phase 4c-3: Server config editing (file-backed)

First real user-facing feature. New plugin interface declares
which files on the install are "server configs", how to parse
them into a structured form, and how to serialise edits back.

**New plugin interface:**
```vb
Public Interface IServerConfigProvider
    Function GetServerConfigs(config As InstanceConfig) _
        As IReadOnlyList(Of ServerConfigDescriptor)
End Interface

Public Class ServerConfigDescriptor
    Public Property Key As String           ' "server-settings"
    Public Property DisplayName As String
    Public Property RelativePath As String  ' "server-settings.json"
    Public Property Format As ConfigFormat  ' Json | Ini | KeyValue
    Public Property Schema As StructuredConfigSchema
    Public Property GenerateDefault As Func(Of String)  ' if missing
End Class
```

The plugin owns parse and serialise for non-JSON formats —
implementations live in `IServerConfigSerializer` so different
file formats stay pluggable. JSON serialiser ships with the
manager (covers Factorio); INI/KeyValue serialisers can be added
when needed for other games.

**Flow:**
1. User opens "Server Settings" tab.
2. Manager calls node's file-download endpoint (Phase 4c-1) to fetch
   the file. If 404, calls plugin's `GenerateDefault` and writes the
   default back so subsequent loads succeed.
3. Plugin parses JSON → field-value map keyed by schema field paths.
4. `StructuredFormBuilder` populates from that map.
5. User edits, hits Save.
6. Manager calls plugin to serialise edits back to JSON, calls
   node's file-upload endpoint to write it back.
7. If the instance is running, manager prompts "Restart instance
   to apply?" (Factorio reads server-settings.json once at start).

**Factorio plugin updates:** implement `IServerConfigProvider`
for `server-settings.json`. Schema follows D4 — three visibility
checkboxes raw, auth section conditionally visible when
`visibility.public=true`, auth method radio synthesises the
username/password/token relationship.

### Phase 4c-4: Save management UI

**New plugin interface:**
```vb
Public Interface ISaveManagementProvider
    Function GetSaveDirectory(config As InstanceConfig) As String
    Function GetSaveFileExtensions() As IReadOnlyList(Of String)
    Function SetActiveSave(config As InstanceConfig,
                            saveFileName As String) _
        As Dictionary(Of String, String)
End Interface
```

`SetActiveSave` returns the field overrides to merge into
`config.CustomFields` before next start. For Factorio:
`{ "SaveFile": "foo.zip", "UseLatestSave": "false" }`.

**New manager UI:** "Saves" tab on the instance config window with:
- DataGridView of saves: name, size, modified time, "active" indicator.
- Buttons: Set as active, Download, Delete, Upload, Generate New.
- Upload: file picker → progress dialog with cancel button. Streams
  via `HttpClient.PostAsync(StreamContent(fileStream))`. Progress
  reported via custom HTTP-handler hook (well-known pattern).
- Download: SaveFileDialog → progress dialog. Streams the response
  body directly to disk.
- Delete: confirmation prompt.
- Generate New: opens the Generate Map tab (Phase 4c-5).

Uploads/downloads use the file ops endpoint from 4c-1.

### Phase 4c-5: Map generation

Generalises the install runner to run plugin-defined one-off
operations. Map generation is the first one.

**New plugin interface:**
```vb
Public Interface IMapGenerationProvider
    Function GetPresets() As IReadOnlyList(Of MapGenPreset)
    Function GetFineTuneSchema() As StructuredConfigSchema
    Function BuildGenerationSteps(presetKey As String,
                                   fineTuneValues As Dictionary(Of String, String),
                                   outputSaveName As String,
                                   instanceConfig As InstanceConfig) _
        As IReadOnlyList(Of InstallStep)
End Interface

Public Class MapGenPreset
    Public Property Key As String           ' "death-world"
    Public Property DisplayName As String   ' "Death World"
    Public Property Description As String
End Class
```

**Reuses the install runner.** Plugin returns `WriteFileStep`
steps for `map-gen-settings.json` and `map-settings.json`, then
a `RunProcessStep` for `factorio.exe --create
saves/<name>.zip --map-gen-settings <path> --map-settings
<path> [--map-gen-seed <n>]`. Same progress/cancel/prompt flow as
install.

Worth renaming the runner from `InstallRunner` to
`OperationRunner` and the entity from `InstallationOperation` to
`Operation`, plus a `Kind` discriminator (`Install | Update |
GenerateMap | …`). Mostly mechanical rename; opens the door for
future one-off ops without parallel infrastructure.

**Manager UI:** Per D3 — Generate Map opens as a sibling tab to
Saves, not a modal:
- Preset dropdown (from `GetPresets`).
- Save name field. Default: based on preset, e.g. "death-world-1".
- Optional "Fine tune" expander showing
  `StructuredFormBuilder(GetFineTuneSchema())`.
- Seed field (separate because Factorio has the dedicated
  `--map-gen-seed` CLI flag — no JSON round-trip needed).
- Generate button → kicks off the operation; progress shown in
  the same tab.
- On success: success state with "Show in saves" link that
  switches back to the Saves tab with the new save selected.

**Factorio plugin updates:** implement `IMapGenerationProvider`
with the four hardcoded presets from D5.

### Phase 4c-6: Polish & docs

- Validation: trying to start an instance with a missing save
  surfaces a friendly error from the manager BEFORE asking the
  node to spawn the process.
- Confirmation prompts on destructive ops (delete save, overwrite
  on upload).
- Update `PowerGSM_Reference.md` with the new contract additions
  and the file-truth model.
- Document the v2 follow-ons that came up in planning:
  - Map exchange string import (D5 v2 note)
  - Per-instance config scope via `{InstanceId}` token (D2 note)

---

## What this changes for existing plugins

**Last Oasis:** nothing. All four new interfaces are opt-in. LO
doesn't expose a JSON server config in the same shape, doesn't
generate maps, and its save concept is different anyway.

**Factorio:** implements all four new interfaces (managed dirs,
server config, saves, map gen). Existing flat `InstanceConfig`
schema (Port, RconPort, etc.) stays — those aren't
`server-settings.json`, they're PowerGSM-side instance
configuration. Becomes the smallest tab in the new tabbed UI.

---

## Suggested first turn in the new chat

Paste this document. Pick a starting phase (4c-1 is the obvious
one — everything else needs it). Ask for the contract additions
for that phase.

A reasonable opening:

> Read Phase4c_Plan.md. All decisions D1–D6 are settled in the doc.
> Start with Phase 4c-1: produce the contract additions and the
> node endpoints. Don't touch the manager UI yet.
