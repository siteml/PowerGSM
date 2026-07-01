# Phase 5l — Manager self-update

Design document for in-place updating of the Manager binary. Three
independently shippable sub-phases (detect → stage → apply) so the
user-visible value lands early and the risky binary-swap work
ships only after the staging path is proven. Node self-update is
explicitly out of scope here; tracked separately as Phase 9 in
ROADMAP.md. Read this first in a new chat; everything below assumes
the conversation is starting fresh.

---

## Status

**5l-1 (detect + notify): shipped.** Background `GitHubReleaseChecker`
(custom semver incl. `-rc` precedence; persisted to the existing
settings key-value bag, not a new table); passive `UpdateDialog`
with a status-bar indicator + **Help → Check for updates...**; skip
this version; install-writeability probe (startup warning + persistent
"read-only install" indicator); **Settings → Updates** (pre-release
opt-in + check interval). Release notes render GitHub-style via
`HtmlRenderer.WinForms` + an in-house `MarkdownToHtml` converter, with
a `MarkdownRenderer` (RichTextBox) → plain-text fallback chain.

**5l-2 (download + stage): shipped.** `UpdateOrchestrator` resolves
the release assets by tag, downloads `SHA256SUMS` + the Manager zip
(streamed, cancellable, with progress), verifies SHA-256, and extracts
to `<install>\.updates\{version}\extracted\`; `UpdateProgressDialog`
drives it and the update dialog gains Download / Update-ready (Apply
disabled pending 5l-3) / Discard. Staged version tracked in the
`update.stagedVersion` setting key. Asset name strips the leading `v`
(LatestVersion carries the raw tag form). Releases predating the
SHA256SUMS pipeline step stage without verification (logged).

**5l-3 (apply): shipped — Phase 5l complete.** `PluginCompatibilityChecker`
(Roslyn dry-run compile of every plugin against a chosen
`Contracts.dll`) with a `CompatReportDialog` (info + apply/acknowledge
modes); the apply engine in `UpdateOrchestrator` (downgrade guard,
`BUILD-INFO.json` read, `apply.cmd` generation, detached spawn,
post-update cleanup, `apply-error.log` surfacing); `ManagerProgram`
`--post-update` handling + on-exit spawn with a clean exit 0 so the
watchdog stands down; the live **Apply** button with its full pre-
flight chain — downgrade guard → automation-in-flight warning →
running-instances warning → staged-contracts compat report → close;
and an update-history table (`UpdateHistoryEntity`, migration
`UpdateHistory`) recording every apply attempt (success on post-update
startup, failure when `apply-error.log` is found), viewable under
**Help → Update History**. The standalone Tools-menu compatibility
entry was dropped as redundant with Plugin Status (which already shows
load results against the *current* contracts); the checker's unique
value — the *staged*-contracts pre-flight — lives in the Apply flow.
Proven via a clean published install applying a real release; the
from→to history row validates on the first apply between two builds
that both carry the recording code (0.3.0 has neither half).

Build-infra fix folded in alongside: the Phase 5m-3 `PublishWatchdog`
MSBuild target now passes `RemoveProperties="TargetFramework"` so a
Manager publish doesn't leak `net8.0-windows` onto the `net8.0`-only
watchdog (which broke VS folder publish, and would have broken the
next tagged release — the watchdog bundling postdates v0.3.0).

The pipeline prerequisites are done: `release.yml` writes
`BUILD-INFO.json` into the Manager zip and emits a `SHA256SUMS`
release asset over all zips. Both are additive and only run on a real
tag push, so they're validated on the next release (or a throwaway
`-rc` tag), not locally.

---

## Goal

The Manager today has no in-band update mechanism. To move from
0.3.0 to 0.3.1, a user has to:

1. Notice that a new release exists (no notification — they have
   to check GitHub)
2. Download the right zip from the Releases page
3. Stop the running Manager
4. Extract the zip over the install directory (careful not to
   stomp on `gsmsettings.json`, `gsm.db`, `Plugins\`, `Logs\`)
5. Relaunch

Every step is friction; the middle step is also where users
break things by extracting in the wrong place or letting the zip
overwrite their config. As PowerGSM gets shared beyond the
author's machines, this friction directly limits adoption and
keeps users on stale builds longer than they should be.

The motivating principle: **a user running the Manager should
discover new versions automatically and apply them with one
click, without losing config, plugins, or history**.

Two specific failure modes the design must rule out:

- A failed apply leaving the user with no working Manager (binary
  half-replaced, won't start). Recovery from this state via
  manual zip-redownload is exactly the friction we're trying to
  eliminate.
- A plugin built against an older Contracts version silently
  failing to load after a Manager update because the new
  Contracts version is incompatible. Users discovering this
  hours later, when an automation rule doesn't fire, is the worst
  case.

---

## Honest assessment of current infrastructure

### What's already there

- **GitHub Actions release pipeline.** `.github/workflows/release.yml`
  produces three zips per release: `PowerGSM-Manager-X.Y.Z-win-x64.zip`,
  `PowerGSM-Node-X.Y.Z-win-x64.zip`, `PowerGSM-Node-X.Y.Z-linux-x64.zip`.
  Triggered by `v*.*.*` tags. Zip naming is stable and parse-friendly.

- **Single-file self-contained Manager publish.** `GSM.Manager.exe`
  is one file containing all .NET dependencies. Native libraries
  are NOT extracted (`<IncludeNativeLibrariesForSelfExtract>false</...>`)
  which keeps startup fast and reduces the file-shuffling
  surface area during update.

- **`GSM.Contracts.dll` excluded from single-file** so Roslyn can
  reference it during plugin compilation. This is the one piece
  of the Manager distribution that lives outside `GSM.Manager.exe`
  and must be swapped alongside it. Already documented in
  `PowerGSM_Reference.md`.

- **Version stamping discipline.** Per `VERSIONING.md`, every
  build assembly carries `Version` / `AssemblyVersion` /
  `FileVersion` / `InformationalVersion` set from
  `Directory.Build.props`. `Assembly.GetExecutingAssembly().GetName().Version`
  gives the running Manager its own version with no extra plumbing.

- **Three-version model (build/protocol/contracts).** Plugin
  compatibility hinges on the contracts version, separate from
  the build version. Plugins declare their requirement via
  `' <RequiresContracts: N>` magic comments. Phase 5l's
  apply-time compat check leans on this directly.

- **Manager-side `/api/version` consumer plumbing** (shipped 5f-2)
  for the Node-version case. The HTTP polling pattern and the
  protocol-comparison logic are similar in shape to what
  GitHub-release polling needs, even though the source is
  different.

- **Database migrations apply automatically at startup** via
  `Database.Migrate()`. Backup-before-migrate already wired
  (`SqliteConnection.BackupDatabase` before pending migrations
  run). The post-update Manager handles schema differences
  transparently.

### What's NOT there

- No GitHub Releases API polling.
- No binary download path for the Manager's own binary.
- No swap-and-restart mechanism.
- No build-metadata sidecar in release zips (need to know the
  new build's Contracts version before applying, so the
  pre-flight compat check can run — see Pipeline changes below).
- No SHA256 / integrity attestation on release artifacts.

### Pipeline changes required

Two small additions to `.github/workflows/release.yml`:

- **`SHA256SUMS` asset** alongside the three zips. Generated
  with `sha256sum` (Linux) or `Get-FileHash` (Windows), one
  line per zip. The release job uploads it as a fourth asset.
  Lets the Manager verify download integrity in 5l-2.

- **`BUILD-INFO.json` inside the Manager zip.** Sidecar file
  at the root of `PowerGSM-Manager-X.Y.Z-win-x64.zip` carrying:
  ```json
  {
    "buildVersion": "0.4.0",
    "protocolVersion": 1,
    "contractsVersion": 2,
    "gitSha": "abc123...",
    "releaseDate": "2026-06-01T..."
  }
  ```
  Lets the Manager read the new build's contracts version
  before unpacking the binary, so the plugin-compat check
  runs against authoritative data rather than parsed release
  notes. The values are already known at build time —
  `Directory.Build.props` + `NodeApiContract.vb` constants —
  so generating the JSON is one extra step in the publish
  workflow.

Both are zero-risk additions: existing release consumers
(humans downloading from the Releases page) see them as extra
files and ignore them.

---

## Design

Three sub-phases, ordered so each one ships independently and
earlier ones don't depend on later ones existing.

### Phase 5l-1 — Detection and notification

Pure read operation. No state mutation beyond a tracking
timestamp in DB. Zero risk to a running Manager; ships even if
nobody ever clicks the "Download update" button.

**`IGitHubReleaseChecker` service.** Registered as a singleton in
`ManagerProgram`. On startup and every 4 hours (configurable),
queries `https://api.github.com/repos/{owner}/{repo}/releases/latest`
and `https://api.github.com/repos/{owner}/{repo}/releases` (for
pre-releases). Parses each release's `tag_name`, strips the
leading `v`, parses as a `Version`. Compares against
`Assembly.GetExecutingAssembly().GetName().Version`.

Rate-limit awareness: GitHub's unauthenticated API allows 60
requests per hour per IP. At 4-hour intervals that's 6/day —
well under the limit, leaves plenty of headroom for the user
to click "Check for updates" manually.

**Pre-release handling.** GitHub marks pre-releases with
`prerelease: true` in the JSON. Default Manager behaviour:
ignore pre-releases. Setting in `gsmsettings.json` (and a
checkbox in Settings UI): "Include pre-release versions" — opt-in.

**Persistence.** A small `UpdateCheckState` row (single-row table
or just a key-value bag) stores:
- Last check timestamp
- Latest version seen
- Whether the user has explicitly skipped this version
- Release body for the latest version (cached for the dialog)

Persistence matters so the indicator survives Manager restart
without immediately re-polling.

**UI surface.** Two anchors:

- **Status-bar indicator.** Bottom-right of the main window. When
  an update is available and not skipped, shows
  `Update available: v0.4.0 →`. Click opens the update dialog.
  When current, shows nothing (no clutter when there's no news).

- **Help menu item.** Always available: `Help → Check for updates...`
  triggers an immediate poll bypassing the 4-hour cadence, then
  opens the dialog regardless of result. Useful for users who
  want to force a check.

**Update dialog (passive).** Modal dialog. Sections:
- Header: "Update available: v0.4.0" (or "You're up to date" if no
  update)
- Release notes from the release body — rendered as plain text
  (markdown not parsed; the body comes from CHANGELOG via the
  release pipeline so it's already human-readable)
- Buttons: `Download update` / `Skip this version` / `Later`

`Skip this version` writes the version to the persisted state;
that version no longer triggers the status-bar indicator (a newer
version still does, transitively un-skipping the skipped one).

**Writeability check.** Self-update only works if the
Manager's install directory is writable by the running
process. Two failure-mode windows: (1) initial install to a
location the user can't write to (Program Files without
elevation, network share, etc.), and (2) state change between
install and update (folder permissions revoked, AV
quarantine, OneDrive sync conflict, Controlled Folder Access
enabled).

Check mechanism: temp-file create-then-delete in AppDir.
Microseconds-cheap; serves as a reliable proxy for binary-
replace-ability without requiring the more invasive
"attempt to overwrite GSM.Manager.exe with itself" path that
could trip AV.

Fires at two moments:

- **At every Manager startup.** If non-writable, surface a
  dismissable warning dialog: "Self-update will not work
  from this install location. Move PowerGSM to a writable
  folder (e.g. `%USERPROFILE%\PowerGSM`) to enable
  automatic updates." Doesn't block startup; doesn't
  prevent any other Manager functionality. The status-bar
  area also gains a small "⚠ read-only install" indicator
  that stays visible across the session for awareness.
- **When an update is detected.** Re-runs the check before
  displaying the update dialog. If non-writable at this
  point, the dialog surfaces the limitation prominently
  ("Update available, but this install can't self-update…")
  and the Download/Apply buttons are disabled with a
  tooltip pointing at the same fix.

The apply.cmd script's own `copy /Y` commands remain the
authoritative authority at apply time — if writeability
changes in the window between detection and apply, the
script fails with a clear written error log and the
still-running Manager surfaces it on next startup.

### Phase 5l-2 — Download and stage

User clicks `Download update`. Manager pulls the zip from the
release's assets (asset name pattern is well-known:
`PowerGSM-Manager-{version}-win-x64.zip`), and the matching
`SHA256SUMS` line is verified against the download.

**Staging folder.** `<AppDir>\.updates\{version}\`. Dot-prefix
hides it from casual filesystem inspection (mostly cosmetic).
Created on first use.

**Download with progress.** `HttpClient.GetAsync` with
`HttpCompletionOption.ResponseHeadersRead` and a `Stream.CopyToAsync`
loop that yields progress updates. Manager-side progress
dialog with cancel button. Cancel mid-download discards the
partial file cleanly.

**Verification.** After download:
- Parse `SHA256SUMS` (downloaded from the same release), find
  the line matching the Manager zip filename, compare against
  computed hash of the downloaded zip.
- If mismatch: discard the staged folder, log the failure, show
  an error dialog with "Try again / Cancel".

**Extraction.** `System.IO.Compression.ZipFile.ExtractToDirectory`
into `<AppDir>\.updates\{version}\extracted\`. The extracted
folder contains:
- `GSM.Manager.exe` (the new binary)
- `GSM.Contracts.dll` (the new contracts assembly)
- `BUILD-INFO.json` (the metadata sidecar)
- Possibly other files the pipeline produces (TBD; verify
  during implementation)

After extraction, the staging folder is structurally complete and
ready to apply. The running Manager is unchanged.

**UI state after staging.** Status-bar indicator changes from
`Update available: v0.4.0 →` to `Update ready: v0.4.0 (Apply now)`.
The dialog (still openable) now has an `Apply update` button
where `Download update` was.

**Staleness handling.** If the user stages an update and then a
newer version releases, the next 5l-1 poll detects the newer
version. UI shows both: "Update ready: v0.4.0 (Apply); newer
available: v0.4.1 (Download)." User can apply the staged one or
discard and download the newer one. The discard button cleans
up the staging folder.

### Phase 5l-3 — Apply

The risky one. User clicks `Apply update`. Four pre-flight
checks plus the swap itself plus rollback.

**Pre-flight check 1: read `BUILD-INFO.json`.** Extract the
new build's contracts version and the path to the staged
`Contracts.dll`. Stashed for the compat check; no user surface.

**Pre-flight check 2: plugin compatibility via dry-run
compile.** Authoritative compat check, replacing the simple
version-marker approach considered in earlier drafts.
For each `.vb` file in `<AppDir>\Plugins\`, the Manager runs
a Roslyn compile against the *staged* `Contracts.dll` (the
new build's contracts assembly, not the currently-loaded
one) and captures the diagnostics. Plugins are partitioned
into:

- **Compatible** — compile succeeds, plugin will load
  cleanly after update.
- **Incompatible** — compile fails with errors. The error
  messages identify which API breakage causes the failure
  (member not found, signature mismatch, etc.) and are
  surfaced verbatim in the report so the operator (or
  plugin author) knows what changed.

Result is shown in a compat-report dialog:

> Plugin compatibility check against v0.4.0:
>
>   ✓ `lastoasis` — compatible
>   ✓ `factorio` — compatible
>   ✗ `custom_conan` — incompatible:
>      error BC30456: 'CharacterId' is not a member of
>      'PlayerSession'
>
> The incompatible plugin(s) will not load after this
> update. Affected instances will fall back to plugin-
> less behaviour (logs still stream, but identity
> resolution and parse rules from those plugins are
> unavailable) until the plugin is updated.
>
> [ ] I understand these plugins will stop loading;
>     proceed anyway
>
> [Cancel] [Apply update]

Behaviour on failure: **soft-warn with explicit
acknowledgement.** The Apply button remains disabled until
the user ticks the acknowledgement checkbox. Hard-blocking
traps users with abandoned plugins; pure inform-only lets
them sleepwalk into broken automation. Acknowledgement gives
the user the choice while making the consequence explicit.

Three trigger surfaces for the same underlying check:

- **Optional button in the update dialog** — `Check plugin
  compatibility` runs the compile pass and shows the report
  inline. Useful for operators who want to investigate
  before committing.
- **Required pre-flight before Apply** — `Apply update`
  is disabled until the compat check has been run at least
  once for the staged version, AND, if any plugin was
  incompatible, the acknowledgement checkbox is ticked.
- **Always-available Tools menu item** —
  `Tools > Test plugin compatibility...` runs the same
  check against the *currently-loaded* `Contracts.dll`
  rather than a staged one. Useful for plugin authors
  iterating on local changes without needing a staged
  update.

All three surfaces share the implementation
(`PluginCompatibilityChecker` service — see Touch points).
The Tools menu item is essentially free once the staged-
compare version exists; the input is just "compile against
which Contracts.dll".

**Pre-flight check 3: automation activity in flight.** Scan
the `AutomationEngine`'s active rule executions. If any rule
is currently executing a long-running action (Sequence,
WaitForServerEmpty, RestartInstance mid-restart, etc.), the
apply dialog prompts:

> An automation rule is currently running:
>
>   - `Restart LO realm at 4am` (Sequence, step 2/4:
>     WaitForServerEmpty, 3 players online)
>
> Continuing the update will interrupt this rule. The
> rule will not auto-resume after the update.
>
> [Wait for completion] [Continue anyway] [Cancel]

Decision rests with the user; no forced blocking. "Wait for
completion" disables the Apply button until the rule
completes (with a small label noting the wait).

**Pre-flight check 4: instance state.** Scan `_liveStates`. If
any instances are `Running` with active SSE streams, the
user is asked to confirm:

> 3 instances are currently running. Applying the update
> will briefly disconnect log streams; instances will
> continue running and the new Manager will reconnect to
> them on startup. Continue?
>
> [Continue] [Cancel]

Game instances themselves are unaffected — they're owned by
the Node, not the Manager. The SSE reconnect already self-
heals via the stream-restart resync path (Phase 5j-3). This
check is informed-consent, not a real risk.

**Generate `apply.cmd`.** Manager writes a Windows batch
script to `<AppDir>\.updates\apply.cmd`:
```batch
@echo off
:waitloop
timeout /t 1 /nobreak >nul
tasklist | findstr /i "GSM.Manager.exe" >nul
if not errorlevel 1 goto waitloop

REM Backup current binaries for rollback
mkdir "<AppDir>\.updates\rollback" 2>nul
copy /Y "<AppDir>\GSM.Manager.exe" "<AppDir>\.updates\rollback\GSM.Manager.exe"
copy /Y "<AppDir>\GSM.Contracts.dll" "<AppDir>\.updates\rollback\GSM.Contracts.dll"

REM Apply
copy /Y "<AppDir>\.updates\{version}\extracted\GSM.Manager.exe" "<AppDir>\GSM.Manager.exe"
if errorlevel 1 goto fail
copy /Y "<AppDir>\.updates\{version}\extracted\GSM.Contracts.dll" "<AppDir>\GSM.Contracts.dll"
if errorlevel 1 goto fail

REM Relaunch
start "" "<AppDir>\GSM.Manager.exe" --post-update {version}

REM Cleanup self
del "%~f0"
exit /b 0

:fail
echo Update apply failed. >> "<AppDir>\.updates\apply-error.log"
exit /b 1
```

The `findstr` poll for `GSM.Manager.exe` in `tasklist`
handles the AV-pause case — the script waits until the
running Manager is truly gone before swapping, rather than
relying on a fixed timeout.

**Spawn detached, then exit gracefully.** Manager calls
`Process.Start` with the script path and
`CreateNoWindow=true`, `UseShellExecute=false`. The spawned
`cmd.exe` is detached from the Manager — when the Manager
exits, the script keeps running.

Then the Manager initiates graceful shutdown:
- Stop services in reverse-start order (AutomationEngine,
  NotificationService, InstanceManager — which cancels all
  SSE streams)
- Final `Database.Migrate()` no-op flush
- Form close

**Post-update startup.** New Manager starts with
`--post-update {version}` argument. Startup logic detects
this:
- Verifies `Assembly.GetExecutingAssembly().GetName().Version`
  matches the expected new version
- Logs `Updated from {oldVersion} to {newVersion}` at
  Information level
- Records the update event in the update-history table
  (success outcome, timestamps)
- Deletes the `.updates\{version}\` staging folder (cleanup;
  rollback folder stays)
- Clears the persisted update-check state's `latestVersion`
  field so the next poll re-detects fresh
- Continues normal startup

**Rollback path.** If the new Manager fails to start (or
starts but the user discovers something broken), they can
revert:

- Manual: copy files from `<AppDir>\.updates\rollback\`
  back over the current binaries. Documented in
  `RELEASE_PROCESS.md` and the in-app dialog.
- Semi-automatic (5l-3 stretch goal): if the new Manager
  detects it was just updated AND a previous run logged a
  startup error AND a rollback folder exists, surface a
  dialog: "The last update may have caused problems.
  Rollback to v0.3.9?" Detection mechanism is fragile
  (heuristic at best); recommend not building this in
  5l-3 v1, instead documenting the manual path and adding
  it later if real users hit the failure mode.

**`apply-error.log`** captures the apply.cmd's stderr-
equivalent. The new Manager (or, on failure, the still-
running old Manager on next launch) reads this and surfaces
any apply errors as a dialog with "Open log folder" / "Try
again" / "Manual rollback instructions".

---

## Touch points (file inventory)

Files that will change:

- **`.github/workflows/release.yml`** — add `BUILD-INFO.json`
  generation step inside the Manager publish job; add
  `SHA256SUMS` generation step in the release job. Pipeline
  prerequisite for 5l-1/5l-2 to work against real releases.

- **`GSM.Manager\Core\PluginCompatibilityChecker.vb`** (new).
  Roslyn dry-run compile service. Takes a target
  `Contracts.dll` path and a list of plugin source files;
  returns per-plugin diagnostics (compiled / failed-with-
  errors). Reused by Phase 5l-3 (against staged contracts)
  AND the Tools menu item (against currently-loaded
  contracts).

- **`GSM.Manager\Core\GitHubReleaseChecker.vb`** (new). Service
  implementation: GitHub API client, version parsing,
  pre-release filtering, polling loop.

- **`GSM.Manager\Core\UpdateOrchestrator.vb`** (new). Coordinates
  the three sub-phase operations: detection → stage → apply.
  Owns the `apply.cmd` generation and process spawn.

- **`GSM.Manager\Data\GsmDbContext.vb`** — add
  `UpdateCheckState` entity + configuration. Single-row table,
  no migration concerns.

- **`GSM.Manager\ManagerProgram.vb`** — DI registration for the
  new services, post-update startup handling (`--post-update`
  CLI arg), staging-folder cleanup.

- **`GSM.Manager\UI\MainForm.vb`** — status-bar indicator,
  Help menu item, dialog wiring.

- **`GSM.Manager\UI\UpdateDialog.vb`** (new). Modal dialog
  with release notes, action buttons, progress display
  during download.

- **`GSM.Manager\UI\SettingsForm.vb`** (existing, modify) —
  pre-release opt-in toggle, "check for updates every N hours"
  numeric.

- **`RELEASE_PROCESS.md`** — document the `BUILD-INFO.json`
  generation step in the procedure, document manual rollback
  steps for users.

- **`PowerGSM_Reference.md`** — add a "Self-update behaviour"
  subsection covering the staging-folder structure, the
  `apply.cmd` mechanism, and how to bypass / disable
  self-update for development environments.

Files that do NOT change:

- Any Node-side file. Phase 5l is Manager-only.
- `INodeClient` contract. No new endpoints.
- Plugin contracts. No new types.
- Existing migrations (the `UpdateCheckState` table is the
  only new schema; one small Add-Migration).

---

## Decisions (resolved 2026-05-27)

Each open question from the original draft, with its
resolution.

1. **Release source configuration — RESOLVED.** Configurable
   now, defaults to the official repo path. Overridable via
   `--update-source` CLI arg and a `gsmsettings.json` field.
   Trivial to add upfront, awkward to retrofit; ship it now.

2. **Pre-release in mixed channels — RESOLVED.** Highest-
   version-wins always, once opted-in. If user is on pre-
   release v0.4.0-rc2 and stable v0.4.0 releases, indicator
   shows the update (rc2 < final in semver). If user is on
   stable v0.4.0 and pre-release v0.4.1-rc1 releases (opt-in),
   indicator shows the update. Explicit downgrade (switching
   from pre-release v0.4.0-rc2 to stable v0.3.9, where stable
   is lower-version) is a separate explicit user action; the
   automatic indicator never proposes a downgrade.

3. **Plugin-compat check granularity — RESOLVED with
   redesign.** The simple
   `RequiresContracts <= newContractsVersion` marker check is
   dropped entirely. Replaced with a **dry-run Roslyn compile**
   of every plugin against the staged `Contracts.dll` from
   the new build. Authoritative result, no false positives,
   no requirement that plugins declare a contracts-version
   marker. Exposed at three surfaces (optional dialog button,
   required pre-flight, always-available Tools menu item).
   Failure behaviour is soft-warn with explicit user
   acknowledgement checkbox. See updated 5l-3 design above
   for full detail.

4. **Update during automation-activity-in-flight — RESOLVED.**
   Third pre-flight check (now fourth, after the new compat-
   check shuffle): if any automation rule is currently
   executing a long-running action (Sequence,
   WaitForServerEmpty, etc.), the apply dialog prompts the
   user to wait, continue anyway, or cancel. Decision rests
   with the user; no forced blocking.

5. **Writeability check for non-writable install — RESOLVED.**
   Option (a): temp-file create-then-delete in AppDir. Fires
   at every Manager startup (microseconds-cheap; warning
   dialog if non-writable, status-bar indicator persists for
   the session) AND when an update is detected (Download/
   Apply buttons disabled if non-writable, dialog explains).
   The apply.cmd script's own copy commands remain the
   authoritative apply-time write check. See 5l-1 design
   above for full detail.

6. **Update history diagnostic view — RESOLVED.** Yes. Comes
   nearly free with the existing `UpdateCheckState` entity
   expanded with a small history child table.
   `Help → Update history…` shows past update events
   (timestamp, from-version, to-version, outcome). Useful for
   postmortem when a user reports "the update broke
   something."

---

## Test plan

### Unit-style (offline)

- Version parsing: `v0.4.0` → `0.4.0`; `v0.4.0-rc1` → `0.4.0-rc1`;
  malformed tag → null + log warning.
- Pre-release filtering: opt-out skips `prerelease: true`
  entries; opt-in includes them.
- Plugin compat check: `RequiresContracts=1` on contracts=1
  build → compatible; `=1` on contracts=2 build → compatible;
  `=2` on contracts=1 build → incompatible; missing marker →
  defaults to 1.
- SHA256 verification: matching hash → pass; mismatch → fail
  + correct error message.
- `apply.cmd` generation: produces well-formed script with the
  current/staged paths substituted correctly; rollback folder
  path is correct.

### Integration (offline simulation)

- Mock GitHub API with fixtures: latest=v0.3.0 (current) → "up
  to date"; latest=v0.4.0 → "update available"; latest=v0.4.0
  but skipped → no indicator.
- Mock release zip: stage it to `.updates\test\`, verify
  extracted contents match expected file list.
- Mock plugin set with one incompatible plugin: pre-flight
  check produces correct warning dialog.

### Integration (real-world dry run)

- On a dev branch, tag and push `v0.3.999-test` with the
  current build content. Confirm release pipeline produces
  the expected zips + `BUILD-INFO.json` + `SHA256SUMS`.
- Run a Manager built from `master`, confirm it detects
  `v0.3.999-test` as an update (because the version
  comparison favors `-test` against `0.3.0`). Stage. Apply.
- Confirm post-update Manager starts cleanly.
- Confirm rollback folder exists and copy-back works.

### Regression

- Manager not configured for self-update (no internet
  connectivity, GitHub API unreachable): startup unaffected,
  no exceptions surface to UI, status-bar indicator absent.
- Manager with pre-existing `.updates\` folder from a previous
  run: cleanup happens on next startup, no clobbering.
- Manager during graceful shutdown: services stop in the
  correct order; SSE streams cancel cleanly; the
  `apply.cmd` waits for full exit before swapping.

---

## Cross-references

- **`VERSIONING.md`** — defines the version model that 5l-1
  consumes (build vs protocol vs contracts), and the
  `Directory.Build.props` location 5l-1 reads from.
- **`RELEASE_PROCESS.md`** — the existing release procedure
  that 5l adds two new artifacts to (`BUILD-INFO.json` and
  `SHA256SUMS`).
- **Phase 5f-2** (shipped in 0.2.x) — Manager-side protocol-
  version checking against the Node. Establishes the HTTP +
  comparison pattern that 5l-1 adapts for GitHub Releases.
- **Phase 5f-3** (shipped, in `VERSIONING.md`) — the plugin
  `' <RequiresContracts: N>` marker that 5l-3's pre-flight
  check reads.
- **Phase 6** (`ROADMAP.md`) — plugin GitHub source + updates.
  Companion piece: Phase 5l updates the Manager binary, Phase 6
  updates plugins. They share the staged-update philosophy
  (download → notify → apply on user action, never automatic)
  and the GitHub-Releases-as-source mechanism. Phase 6
  could reuse `IGitHubReleaseChecker` if generalised — worth
  noting at Phase 6 design time rather than retrofitting.
- **Phase 9** (`ROADMAP.md`) — Node self-update. Different
  problem (the Node owns running game instances; the Manager
  doesn't). Manager-orchestrated; uses the version-comparison
  primitives from 5l-1 but a completely different apply
  mechanism.
- **Backlog: nothing specific** — no deferred items in
  Backlog.md that this phase resolves.

