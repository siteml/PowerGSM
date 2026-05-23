# Phase 5f — Versioning & Release Story

Design document for establishing a coherent versioning scheme and
release process before sharing PowerGSM with anyone else. Read
this first in the new chat; everything below assumes the
conversation is starting fresh.

---

## Goal

Solidify three concerns that have been implicit and need to
become explicit before the project ships beyond the author's
hardware:

1. **Build/release versioning** — every assembly stamped with
   a coherent version, surfaced in UI and on the wire so users
   reporting issues can say "I'm on 0.4.2."

2. **Wire-protocol versioning** — manager and node negotiate
   compatibility on connect. Mismatches surface as actionable
   warnings rather than mysterious 404s.

3. **Plugin contract versioning** — plugin source files
   declare what contracts version they target. Manager warns
   on mismatch instead of dumping a Roslyn compile error.

Plus the umbrella concern that ties them together: **a release
process** so version numbers actually correspond to artifacts
people can install.

The motivation is timing: sharing-with-others is on the horizon,
and it's much cheaper to establish this now (one user, one
machine, full control) than after the first external user is
running a different version than they think they are.

---

## Honest assessment of current state

Currently there is:

- **No version stamping anywhere.** No `<Version>`,
  `<AssemblyVersion>`, `<FileVersion>`, or
  `<InformationalVersion>` in any of the five `.vbproj` files.
  Built assemblies report `1.0.0.0` by default — meaningless.
- **No protocol version on the wire.** `/api/version` returns
  the build version (when one exists) but doesn't separate
  protocol-compat from build identity. A manager built 6 months
  later that talks to an old node has no way to know
  endpoint contracts have changed.
- **No plugin-contract version on the contracts assembly.** A
  plugin compiled against an old `IGamePlugin` shape and a
  manager loading it via Roslyn against the new shape produces
  whatever Roslyn produces — works if changes are additive,
  fails opaquely if not.
- **No release process.** "Releases" today are
  `dotnet publish` runs done locally. Nothing tags commits,
  nothing produces zip artifacts, nothing connects a version
  string to a downloadable build.
- **No CHANGELOG.** What changed between any two builds is
  whatever git log says, which is fine for the author but
  opaque to anyone else.

Status as of writing: **0.0.0**. Nothing's named, nothing's
released. Everything below is about establishing the framework
before bumping to 0.1.0 as the first "named" version.

---

## Resolved design decisions

These were settled in the planning conversation. Reasoning kept
so the next chat doesn't re-litigate.

### D1. Version scheme: pre-1.0 SemVer

Format: `0.MINOR.PATCH` while pre-1.0.

- **MINOR** bumps for new features, behavioural changes that
  may break compatibility with the previous version
  (configuration migrations needed, protocol changes, etc.),
  and notable milestones.
- **PATCH** bumps for bug fixes and small additive changes
  unlikely to break existing setups.
- **MAJOR** stays at 0 until the project is declared stable.
  When that happens, version becomes `1.0.0` and the project
  commits to SemVer's full breaking-change discipline (MAJOR
  bumps for any breaking change).

This matches the user's stated meaning of `0.x.y` (x = "complete
features shipped/changed in a major way that will usually break
compatibility with prior versions", y = "small changes in
functionality that may not necessarily break functioning") —
exactly SemVer's MINOR/PATCH semantics, just with the
pre-1.0 license to break things at MINOR granularity.

Build metadata or pre-release tags can be added when needed
(`0.4.0-rc1`, `0.4.0+build.42`) but aren't required from the
start.

### D2. Single source of truth: Directory.Build.props

A single `Directory.Build.props` at the solution root sets
`<Version>`, `<AssemblyVersion>`, `<FileVersion>`, and
`<InformationalVersion>` for all five projects. Bumping is
one-edit-one-file, and every build stamps consistently.

`AssemblyVersion` follows the .NET convention of `MAJOR.MINOR.0.0`
(strong-naming would care about this; we don't have strong
names but the convention costs nothing). `FileVersion` and
`Version` carry the full `0.MINOR.PATCH`.
`InformationalVersion` carries the full string including any
pre-release tag and optionally the git short-SHA via
`SourceRevisionId` (set automatically by the SDK when
`IncludeSourceRevisionInInformationalVersion` is true).

### D3. Three orthogonal version axes

Build, protocol, and contracts versions are tracked separately
because they change for different reasons.

- **Build version** (`0.MINOR.PATCH`) — what humans cite. Bumps
  on every release. Surfaced everywhere.
- **Protocol version** (single integer, currently 1) — bumps
  only when the manager↔node REST contract changes in a
  breaking way (endpoint removed, request/response shape
  changed, semantics altered). Additive changes (new optional
  fields, new endpoints) do NOT bump it.
- **Contracts version** (single integer, currently 1) — bumps
  only when `GSM.Contracts` makes a breaking change to plugin-
  facing types. Adding members to interfaces is non-breaking
  (existing plugins still compile). Removing or changing
  signatures bumps it.

Most build-version bumps will leave protocol and contracts
versions unchanged. The integers stay small (we'll be at
protocol v2 or v3 for years, not v47).

### D4. Compatibility matrix policy

Same-MINOR manager and node MUST work together. Cross-MINOR
should warn but try to function:

- Manager has higher protocol version than node →
  "node is older than expected, some features may not work"
  warning at connection time. Manager tries operations; ones
  using newer endpoints get a clean error from the node's 404
  rather than a crash.
- Node has higher protocol version than manager →
  "node is newer than expected" warning. Manager only uses
  endpoints it knows about; node's newer features go unused.
- Same protocol version → silent.

This is intentionally permissive. Strict version-locking
("manager v0.4 refuses to talk to node v0.3") is hostile to
users who can't update both sides simultaneously. Warning +
graceful degradation is friendlier.

For plugin contracts the policy is similar but stricter: a
plugin declares its target contracts version; if the running
contracts version is the same MAJOR (currently always 1) the
plugin loads with a debug log; if higher (newer manager loading
older plugin) it loads with an info log; if the plugin targets
a newer contracts version than the manager has, it FAILS to
load with a clear error message rather than getting a Roslyn
compile blowup.

### D5. Release process: tagged git + GitHub Actions building artifacts

Manual local `dotnet publish` works for development but doesn't
scale to "send this to someone." Target state:

1. Bump `<Version>` in `Directory.Build.props`.
2. Update `CHANGELOG.md` with the new section (Keep-A-Changelog
   format).
3. Commit "Release 0.MINOR.PATCH".
4. Tag with `v0.MINOR.PATCH`.
5. Push tag to GitHub.
6. GitHub Actions builds Manager + Node + dependencies, packs
   them into named zips (`PowerGSM-Manager-0.4.0-win-x64.zip`,
   `PowerGSM-Node-0.4.0-win-x64.zip`), creates a GitHub
   Release with the CHANGELOG section as release notes.

The tag IS the release. No "untag and rebuild" — if the artifact
is wrong, bump PATCH and tag a new release.

GitHub Actions is the natural fit because the repo is already
on GitHub (`.github/` directory exists; not yet checked what's
in it). If there's no Actions workflow today, this phase
introduces one.

### D6. CHANGELOG: Keep-A-Changelog format, manually maintained

`CHANGELOG.md` at the solution root, updated as part of each
release commit. Keep-A-Changelog format
(<https://keepachangelog.com/>) — sections are Added / Changed
/ Deprecated / Removed / Fixed / Security per release. Each
release links to the git tag.

Manual maintenance rather than tooling-driven. The author is the
only committer and writes good commit messages; auto-generating
from commit log adds tooling complexity without proportional
benefit at this scale. Revisit if multiple committers join.

---

## Proposed phasing

Each phase ends at a shippable state. Earlier phases unblock
later ones; phase 5f-1 is the minimum viable versioning story
and 5f-2/5f-3 add depth.

### Phase 5f-1: Build versioning + CHANGELOG + manual publish workflow

The minimum that establishes "version 0.1.0" as a real thing.

**Files added:**
- `Directory.Build.props` at solution root with `<Version>`,
  `<AssemblyVersion>`, `<FileVersion>`,
  `<InformationalVersion>`. Initial value `0.1.0`.
- `CHANGELOG.md` at solution root with an `[0.1.0]` section
  capturing everything since project start (one-time effort
  to backfill; subsequent releases just append).
- `VERSIONING.md` at solution root explaining the 0.x.y policy
  and the three version axes (build/protocol/contracts) for
  anyone reading the repo cold.

**Code changes:**
- Manager: Help → About dialog showing Build version,
  Contracts version, Protocol version. New form
  (`AboutForm.vb`) opened from the menu bar. Also adds a
  small status-bar version indicator on the main window for
  passive visibility.
- Manager: log the build version on startup (one
  `LogInformation` line so log files self-identify what
  version produced them — useful for diagnosis).
- Node: log build version on startup, same reasoning.
- Node `/api/version` endpoint extended: returns
  `{ build, protocolVersion, contractsVersion }` JSON. Today's
  shape is preserved for backward compat (still returns at
  least the build version field) but adds the protocol/
  contracts integers.
- `NodeApiContract.vb`: add `ProtocolVersion` constant
  (initial value 1) and `ContractsVersion` constant (initial
  value 1). These are the canonical integers — bumped by hand
  when breaking changes ship.

**Acceptance:** clean build produces assemblies stamped 0.1.0.0
(AssemblyVersion) and 0.1.0 (FileVersion). Help → About in
the manager shows "PowerGSM 0.1.0 (Protocol v1, Contracts v1)".
Node startup log line says "GSM.Node 0.1.0 starting". Hitting
`/api/version` returns the new JSON shape.

### Phase 5f-2: Protocol version negotiation + connection warnings

Manager checks node's protocol version on connect and surfaces
mismatches.

**Code changes:**
- `NodeHttpClient`: on first successful call to a node, read
  `protocolVersion` from `/api/version`, cache it on the node
  entity (new `LastSeenProtocolVersion` column, EF migration).
  Compare against the manager's compiled-in
  `NodeApiContract.ProtocolVersion`.
- Manager UI: new node-status indicator showing protocol
  compatibility. Same protocol = green. Manager-newer = yellow
  ("Node may not support all features"). Manager-older = yellow
  ("Manager doesn't support all node features"). Connection
  failure = red (existing behaviour, unchanged).
- Specific operations that depend on protocol-newer features
  check the cached version and either skip silently or surface
  a tooltip explaining why a button is disabled. Most
  operations don't need this; only ones that use endpoints
  added in a specific protocol version do.

**Documentation:**
- `VERSIONING.md` gains a "Protocol Compatibility" section
  documenting which manager versions correspond to which
  protocol versions, and what each protocol bump introduced.

**Acceptance:** running a manager-0.2.0 against a node-0.1.0
shows the yellow warning. Running matched versions is silent.
Running with any feature that requires protocol v2 against a
v1 node produces a clean disabled-button-with-tooltip rather
than a runtime exception.

### Phase 5f-3: Contracts versioning for plugins

Plugin source files declare what contracts version they target.
Manager validates on load.

**Code changes:**
- `GSM.Contracts`: add `<Assembly: ContractsVersion(1)>` via
  a new `ContractsVersionAttribute` class. Manager reads this
  on startup to know its own running contracts version.
- New plugin convention: at the top of every plugin source
  file, a magic comment:
  `' <RequiresContracts: 1>` (parsed at load time). Plugins
  without this comment default to "1" with a warning logged.
- `PluginRegistry.ReloadAll`: parses the magic comment from
  each `.vb`, compares to running contracts version, decides:
  - Same version → load silently
  - Plugin requires older → load with debug log "plugin
    targets contracts v1, manager runs v2 — should be
    compatible"
  - Plugin requires newer → fail to load this plugin (others
    still load) with `OrphanWarning`-style error: "plugin
    targets contracts v3, manager only runs v2. Update the
    manager or use a plugin compiled for v2."
- `PluginStatusForm` shows the declared contracts version per
  plugin alongside load status.

**Why a magic comment, not an attribute?** Plugins are .vb
source compiled at runtime. Attributes work but require the
plugin to import the right namespace and reference the right
type, which is more ceremony than a comment. The comment is
parseable cheaply in `PluginRegistry` before invoking Roslyn,
so we can fail-fast on version mismatch rather than after a
costly compile.

**Existing plugins** (LO, Factorio): updated to declare
`' <RequiresContracts: 1>` as part of this phase.

**Acceptance:** loading the existing plugins shows their
declared contracts version in the Plugin Status form. Hand-
crafting a plugin with `' <RequiresContracts: 999>` produces
a clean refusal with the right error message; doesn't crash
the loader.

### Phase 5f-4: GitHub Actions release workflow

The "tagged-commit-becomes-released-artifact" pipeline.

**Files added:**
- `.github/workflows/release.yml`: triggered on push of
  `v*.*.*` tags. Builds Manager + Node in Release config,
  publishes self-contained win-x64 binaries, zips them,
  creates a GitHub Release with the CHANGELOG section as
  body, uploads zips as release assets.
- `.github/workflows/ci.yml` (if not already present):
  triggered on push to main and PRs. Just builds + verifies
  no compile errors. Faster than release; runs on every
  commit.

**Decisions to make in this phase** (deliberately deferred from
the planning conversation since they're mechanical rather than
architectural):

- Self-contained vs framework-dependent publishes
  (recommend self-contained for users who don't want to
  install .NET 8 runtime separately).
- Single-file vs loose binaries (we have explicit
  `<ExcludeFromSingleFile>` on Contracts already; the existing
  setup works).
- Whether to publish to NuGet (the manager isn't a library;
  plugins compile against `GSM.Contracts` from disk via
  Roslyn, not NuGet). No NuGet publishing for v1.

**Acceptance:** tagging `v0.1.0` produces a GitHub Release
with `PowerGSM-Manager-0.1.0-win-x64.zip` and
`PowerGSM-Node-0.1.0-win-x64.zip` artifacts. Downloading,
unzipping, and running each produces a functional manager
and node respectively. CHANGELOG `[0.1.0]` section appears
as the release body.

### Phase 5f-5: Release-checklist polish

Final niceties that make the release process repeatable
without thinking:

- `RELEASE_PROCESS.md` documenting the steps: bump version,
  update CHANGELOG, commit, tag, push tag, verify Actions
  succeeded, verify release notes look right.
- Optional: a small PowerShell or bash script that does
  bump-version-and-update-changelog interactively.
- Auto-update check (the manager pings the GitHub API for
  the latest release and surfaces a "new version available"
  notification). Optional and probably v2 — most game-server
  managers do this and it's polite, but it's not on the
  critical path.

---

## What this changes for existing functionality

**No runtime behaviour changes from 5f-1.** Just adds version
metadata and a Help dialog. Existing builds work identically.

**5f-2 adds the warning indicator** but keeps everything
functional — no "refuse to connect" behaviour. Mismatched
versions still talk; the warning is informational.

**5f-3 changes plugin loading subtly** — plugins without the
magic comment work but log a warning. Existing LO and Factorio
plugins get the comment added as part of the phase, so no
warning on the canonical plugins.

**5f-4 doesn't change the running app at all** — purely
ops/release plumbing.

**Phases 4c, 5d, 5e and beyond** benefit indirectly: every
phase that adds new node endpoints can bump
`NodeApiContract.ProtocolVersion` as part of its work, with
the warning machinery from 5f-2 surfacing the change. Same for
plugin contract additions.

---

## Suggested first turn in the new chat

Paste this document. All decisions D1–D6 are settled in the doc.
Start with Phase 5f-1.

A reasonable opening:

> Read Phase5f_Plan.md. All decisions D1–D6 are resolved in the
> doc. Start with Phase 5f-1: produce Directory.Build.props,
> CHANGELOG.md, VERSIONING.md, and the manager's About dialog.
> Initial version is 0.1.0.
