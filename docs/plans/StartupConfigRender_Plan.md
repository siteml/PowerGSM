# Startup Config Render — Plan

Cross-cutting Manager + Contracts feature. Phase number TBD by Site.
Drafted 21 Jun 2026 out of the Windrose Slice 2 work (D2) + Conan's
server-name garbling.

---

## 1. Problem

PowerGSM has exactly two field→runtime bridges today (confirmed by reading
`InstanceManager.StartInstanceAsync` + the contract):

- `BuildLaunchArguments` — instance `CustomFields` → command line.
- `IFileGenerationProvider` — a user-triggered "Generate" button.

Neither pushes instance config into a game's **own config file** automatically
at start. Two real consequences, same root cause:

1. **File-only games can't use the node port allocator.** The allocator picks
   free values for `IsPort` fields declared in `GetInstanceConfigSchema` and
   stores them in `CustomFields`. Those only reach the game through launch
   args. Windrose has **no launch args** — its `DirectConnectionServerPort`
   lives only in `ServerDescription.json` (the file editor), so the allocator
   can't see or manage it. (This is Windrose Decision **D2**.)
2. **Arg-passed text garbles.** Conan's `ServerName` passed on the command line
   gets mangled (spaces / unicode / quoting). The identical value is clean when
   the engine reads it from its config file.

Both want the same missing capability: **render selected instance-config values
into the game's config file just before launch, preserving everything else in
the file.** It will keep cropping up (any file-config game, any garble-prone
text value), so it belongs in the Manager, not bolted onto one plugin.

---

## 2. Decision D3 — startup config render hook

Add an **opt-in side-interface** (same pattern as `ILogParser` / `IModManager`
— VB can't add default members to `IGamePlugin` without breaking every existing
plugin). The Manager invokes it inside `StartInstanceAsync`, after the config-
layer merge and before sending `StartInstanceRequest`, using the **same node
file read/write endpoints the file editor already uses**.

### 2.1 Interface (GSM.Contracts)

```vb
Public Interface IStartupFileProvider
    ''' Relative paths (under the install dir) the plugin wants to
    ''' (re)write from instance config at start. Cheap; called each start.
    Function GetStartupFiles(instanceConfig As InstanceConfig) _
        As IReadOnlyList(Of String)

    ''' Given the file's CURRENT on-disk text ("" if absent), return the new
    ''' content with instance-config values injected, preserving everything
    ''' else. Return Nothing (or the unchanged text) to skip the write.
    ''' Return Nothing when existingText is empty if the game must generate
    ''' the file itself first (Windrose: skip on first launch).
    Function RenderStartupFile(relativePath As String,
                                instanceConfig As InstanceConfig,
                                existingText As String) As String
End Interface
```

Plugins reuse their own format helpers inside `RenderStartupFile` — Windrose
already has the JSON `ReadFileToValues` / `WriteValuesToFile` round-trip; Conan
has its INI helpers. **No new per-format machinery on the Manager side.**

### 2.2 Manager hook (`StartInstanceAsync`)

After `MergeConfigLayers` → `CustomFields` and plugin resolution, before
building `StartInstanceRequest`:

```
If TypeOf plugin Is IStartupFileProvider Then
    For Each relPath In plugin.GetStartupFiles(instanceConfig)
        existing = <node file GET>(relPath)        ' "" if 404 / absent
        rendered = plugin.RenderStartupFile(relPath, instanceConfig, existing)
        If rendered IsNot Nothing AndAlso rendered <> existing Then
            <node file PUT>(relPath, rendered)
    Next
End If
```

Reuses the node file endpoints that back `IInstanceFileEditorProvider` (the
editor reads to populate the form and writes on save, so both GET and PUT
already exist + share the allowed-roots validation). Idempotent: render every
start, PUT only on a diff.

### 2.3 Dual-ownership rule (the catch)

A value rendered at start MUST have a **single editable home**, or the Settings-
file editor and the Configuration tab fight (the start render runs last and
would silently revert editor edits). So: a field that becomes Configuration-tab
owned (in `CustomFields`) is **removed from the file-editor schema**. Net effect
per game: networking/ports move to the Configuration tab; descriptive/world
fields stay in the file editor.

---

## 3. Consumer migrations

### Windrose (resolves D2)
- Add `DirectConnectionServerPort` to `GetInstanceConfigSchema` as
  `IsPort=True` → allocator manages it. (Optionally `UseDirectConnection` too.)
- Implement `IStartupFileProvider`: `GetStartupFiles` → `{ "R5/ServerDescription.json" }`;
  `RenderStartupFile` reads the existing JSON, injects the port (only when
  direct mode is on), returns merged text; returns `Nothing` when `existingText`
  is empty (let the server create the file on first launch — port applies on the
  2nd start).
- **Remove** `DirectConnectionServerPort` from the Settings-file editor schema
  (now Configuration-owned).

### Conan (fixes server-name garbling + password placement)
- Move `ServerName` out of the launch-args path AND `ServerPassword` off the
  Engine.ini file editor; render both into `Engine.ini` `[OnlineSubsystem]`
  via `IStartupFileProvider`. Drop `ServerName` from `BuildLaunchArguments`.
- Remove the structured "Network (Engine.ini)" editor tab (single-ownership);
  raw `Engine.ini` stays editable via the `.ini` browser.
- `ServerName` always writes (blank → default name). `ServerPassword` is
  set / keep / clear via a `ClearServerPassword` checkbox: non-empty writes
  it, blank + unticked preserves the file's value (migration-safe), blank +
  ticked writes empty. Extract the INI section-writer into a shared
  `WriteIniSection` used by both the editor and the render.

---

## 4. Open questions (decide before/at implementation)

- **O1 — failure policy. RESOLVED: proceed + warn.** A file GET/PUT hiccup
  logs a warning and the launch proceeds with the file's last value; worst case
  the operator stops the instance and re-evaluates. No reason to block a launch
  that would otherwise be fine.
- **O2 — ContractsVersion. RESOLVED: stays 2 (no bump).** The constant is
  already 2 (Phase 7 utility surface) and v2 (0.3.0) was never released, so the
  render surface folds into the still-in-dev v2 — there's no released v2 Manager
  to gate against. Adopting plugins declare `requiresContracts="2"`.
- **O3 — Edit-Instance immediacy.** Render runs at start only (file updates on
  next launch). Fine for ports, acceptable for names. Don't also write on
  Edit-Instance save — keeps one write path.

---

## 5. Slice plan (confirm-gated)

- **A — Contracts. DONE.** `IStartupFileProvider` added next to
  `IInstanceFileEditorProvider`; `ContractsVersion` stays 2. Built clean.
- **B — Manager. DONE.** `ApplyStartupFileRendersAsync` in `InstanceManager`,
  called from `StartInstanceAsync` after the config-layer merge and before the
  start request. Reuses the editor's `DownloadFileAsync`/`UploadFileAsync` +
  allowedRoots/extension derivation + 404→empty handling; proceed-and-warn (O1).
  No behaviour change until a plugin implements the interface.
- **C — Windrose. DONE & VERIFIED.** Live run confirmed: a freshly-allocated
  port (50104) rendered into `ServerDescription_Persistent` and bound by the
  engine in direct mode, with `CommandLine = ' -log'` (no args carrying config)
  — proving allocator → Configuration → render → file → server end to end.
  Header bumped to
  `requiresContracts="2"`; class implements `IStartupFileProvider`.
  `DirectConnectionServerPort` (`IsPort`) + `UseDirectConnection` moved to
  `GetInstanceConfigSchema` (allocator-managed) and removed from the file-editor
  schema/read/write (single-ownership). `RenderStartupFile` writes both into
  `ServerDescription_Persistent` at start (skips when the file is absent so the
  server creates it first; port stamped only when direct mode is ON). Verify on
  reload: allocator assigns/validates the port; two direct-mode servers on one
  node get distinct ports; ServerName/region/etc. still editable in the file tab.
- **D — Conan. DONE.** `ServerName` (off the launch URL) and `ServerPassword`
  (off the Engine.ini editor) are now Configuration-tab fields rendered into
  `Engine.ini` `[OnlineSubsystem]` at start; the "Network (Engine.ini)" editor
  tab is removed (raw `Engine.ini` still editable via the `.ini` browser).
  Header bumped to `requiresContracts="2"`; class implements
  `IStartupFileProvider`. The INI section-writer was extracted from
  `WriteValuesToFile` into a shared
  `WriteIniSection(targetSection, schema, values, existingText)` the render
  reuses — omitting a key from the render schema leaves the file's existing
  value untouched, which is how preserve-if-blank works. `ServerName` always
  writes (blank → default name). `ServerPassword` is set / keep / clear via a
  new `ClearServerPassword` checkbox: non-empty writes it; blank + unticked
  preserves the existing Engine.ini value (so an upgrade from the editor-tab
  version doesn't wipe a set password); blank + ticked writes empty (open
  server). Render skips when `Engine.ini` is absent (server creates it first;
  values apply from the 2nd start). Verify on reload: a name with
  spaces/unicode comes through clean; password set/keep/clear behaves; the
  Network tab is gone; no `AESDecryptionFailed` on connect.
