# Versioning

PowerGSM tracks three orthogonal version numbers. They change for
different reasons, on different schedules, and answer different
questions. Knowing which one is bumping when matters for users
mixing and matching binaries — especially as the project starts
being shared beyond the author's hardware.

The three:

- **Build version** — `0.MINOR.PATCH`. The public number humans
  cite. Stamps every assembly. Bumps every release.
- **Protocol version** — single integer. Manager↔Node REST
  contract identifier. Bumps only when the wire shape changes
  in a breaking way.
- **Contracts version** — single integer. Plugin-facing types
  in `GSM.Contracts`. Bumps only when those types change in a
  breaking way.

Most build-version bumps leave protocol and contracts versions
untouched. Expect protocol to be at v2 or v3 a year from now,
not v47.

---

## Build version (`0.MINOR.PATCH`)

The build version follows pre-1.0 [Semantic Versioning](https://semver.org/).
While the leading `MAJOR` is `0`, the project is not yet committed
to SemVer's full breaking-change discipline:

- **PATCH** bumps for bug fixes and small additive changes that are
  unlikely to break existing setups. Reading "I went from 0.4.2 to
  0.4.3" should give a user no reason to expect to change anything.
- **MINOR** bumps for new features, behavioural changes, configuration
  migrations, protocol changes, and notable milestones. Pre-1.0,
  MINOR releases ARE allowed to break compatibility with the previous
  version — this is the licence pre-1.0 SemVer grants. The CHANGELOG's
  "Changed", "Removed", and "Deprecated" sections should make any
  breaking change explicit.
- **MAJOR** stays at `0` until the project is declared stable. When
  that happens, the version becomes `1.0.0` and from then on MAJOR
  bumps for any breaking change, MINOR for any backward-compatible
  feature, PATCH for any backward-compatible fix.

Pre-release tags (`0.4.0-rc1`, `0.4.0-beta`) and build metadata
(`0.4.0+build.42`) are allowed but optional. The build always carries
a git short-SHA in `InformationalVersion` automatically when the SDK
detects a git repo.

### Where the build version lives

A single `Directory.Build.props` at the solution root sets:

| Property               | Value at 0.1.0 | Bumps on |
| ---------------------- | -------------- | --- |
| `Version`              | `0.1.0`        | every release |
| `AssemblyVersion`      | `0.1.0.0`      | MINOR or MAJOR only |
| `FileVersion`          | `0.1.0.0`      | every release |
| `InformationalVersion` | derived        | every release (SDK-derived from `Version` + git SHA) |

`AssemblyVersion` follows the .NET convention of `MAJOR.MINOR.0.0` —
it's a coarser tracker than `FileVersion` so PATCH-only bumps don't
appear as a new assembly identity. We don't sign assemblies today,
so this is convention rather than necessity, but it costs nothing.

### Bumping build version

For a PATCH release (e.g. 0.1.0 → 0.1.1):

1. Edit `Directory.Build.props`: bump `<Version>` and `<FileVersion>`
   (e.g. `0.1.0` → `0.1.1`, `0.1.0.0` → `0.1.1.0`). Leave
   `<AssemblyVersion>` alone.
2. Add a new `## [0.1.1]` section to `CHANGELOG.md`.
3. Commit, tag `v0.1.1`, push the tag.

For a MINOR release (e.g. 0.1.5 → 0.2.0):

1. Edit `Directory.Build.props`: bump all three of `<Version>`,
   `<AssemblyVersion>`, and `<FileVersion>` (e.g. `0.1.5` → `0.2.0`,
   `0.1.0.0` → `0.2.0.0`, `0.1.5.0` → `0.2.0.0`).
2. Add a new `## [0.2.0]` section to `CHANGELOG.md`. Call out any
   breaking changes prominently in "Changed" / "Removed".
3. Consider whether the change warrants a protocol or contracts
   version bump (see below). If yes, edit `NodeApiContract.vb`
   accordingly in the same release.
4. Commit, tag `v0.2.0`, push the tag.

The release-tooling phases (5f-4, 5f-5) will eventually introduce a
helper script that does steps 1–3 interactively. Until then, the
edits are by hand.

---

## Protocol version

Defined in `NodeApiContract.vb` as the constant
`NodeApiContract.ProtocolVersion`. The Node returns it from
`/api/version` so the Manager can compare against its own
compiled-in copy on connect.

Bumps **only** when the Manager↔Node REST contract changes in a
breaking way:

- An endpoint is removed or its URL changes.
- An existing request DTO field is removed, renamed, or its meaning
  changes.
- An existing response DTO field is removed, renamed, or its
  semantics change.
- An endpoint's HTTP method changes.

Does **not** bump when:

- A new endpoint is added (older Managers won't call it; older Nodes
  return 404 which the new Manager handles).
- A new optional field is added to an existing DTO (older parsers
  ignore unknown fields; older serialisers don't send them).
- An internal node implementation changes.

### Compatibility policy

Same protocol version → silent.

Manager protocol > Node protocol → "Node may not support all
features" warning. Manager tries operations; ones using newer
endpoints get a clean error from the node's 404 rather than a crash.

Manager protocol < Node protocol → "Node is newer than expected"
warning. Manager only uses endpoints it knows about; node's newer
features go unused.

This is intentionally permissive. Strict version-locking ("Manager
v0.4 refuses to talk to Node v0.3") is hostile to users who can't
update both sides simultaneously. Warning + graceful degradation is
friendlier.

The Manager-side protocol-check infrastructure shipped in phase
5f-2: when the Manager opens a node-detail panel it calls the
unauthenticated `/api/version`, compares the returned
`protocolVersion` against its own compiled-in
`NodeApiContract.ProtocolVersion`, and renders a one-line indicator
below the node's host/port:

- **Compatible** — dark green, names the protocol version and the
  node's build string.
- **Node older** or **Node newer** — dark orange, names both
  versions so users can see exactly which side to update.
- **Could not contact node** — firebrick red. Covers connect
  failures, HTTP failures, and pre-5f-1 nodes whose `/api/version`
  doesn't return JSON in the expected shape.

The observed protocol version is also persisted to
`NodeEntity.LastSeenProtocolVersion` so future feature-gating
logic can decide whether to enable a button without waiting on a
fresh round trip. Pre-5f-1 nodes return zero for `protocolVersion`
(the field didn't exist); the indicator treats zero as "older than
this manager, no protocol version reported" rather than rendering
a confusing "v0".

### Protocol history

| Protocol version | First Manager release | Notes |
| ---------------- | --------------------- | ----- |
| (none)           | pre-0.1.0             | `/api/version` returned `application`/`version`/`runtime` only. Manager treats this as "node older". |
| 1                | 0.1.0                 | Initial named version. `/api/version` adds `build`, `protocolVersion`, `contractsVersion` fields. All endpoints documented in `NodeApiContract.vb`. |

---

## Contracts version

Defined in `NodeApiContract.vb` as the constant
`NodeApiContract.ContractsVersion`. Plugins that compile against
`GSM.Contracts` declare which contracts version they target.

Bumps **only** when types in `GSM.Contracts` change in a breaking
way:

- A method or property is removed from an interface.
- A method signature changes (parameters, return type).
- An enum member is removed or renamed.
- A class is removed or renamed.

Does **not** bump when:

- A new method or property is added to an interface that has a
  default implementation (members without defaults still bump).
- A new enum member is added.
- A new class is added.
- An optional parameter is added to an existing method.

### Plugin compatibility

Plugins are `.vb` source files compiled at runtime by Roslyn. They
declare their target contracts version via a magic comment at the
top of the file:

```vb
' <RequiresContracts: 1>
```

The Manager reads this before invoking Roslyn so a version mismatch
fails fast with a clear error rather than dumping a Roslyn compile
error. Plugins without the comment default to "1" with a warning
logged.

Same contracts version → load silently.

Plugin requires older contracts → loads with a debug log noting the
mismatch. Should be compatible since contracts only break on a
contracts-version bump.

Plugin requires newer contracts → fails to load this plugin (others
still load). Error message names the version mismatch and tells the
user to update the Manager or use a plugin compiled for the running
contracts version.

The plugin-side declaration and the Manager-side validation ship in
phase 5f-3.

### Contracts history

| Contracts version | First introduced in | Notable changes |
| ----------------- | ------------------- | --------------- |
| 1                 | 0.1.0               | Initial baseline. All types documented in `GSM.Contracts/*.vb`. |

---

## Bumping protocol or contracts version

Both are integer constants in `NodeApiContract.vb`:

```vb
Public Const ProtocolVersion As Integer = 1
Public Const ContractsVersion As Integer = 1
```

Bumping is one edit: increment the integer. The bump rides along with
whatever build version (always MINOR, never PATCH) introduces the
breaking change. The CHANGELOG section for that build version should
call out the protocol or contracts bump explicitly under "Changed".

The Protocol history and Contracts history tables above should grow
a new row at the same time so anyone reading this document cold can
understand what each integer means.

---

## Release process

Releases are produced by GitHub Actions when a `v*.*.*` tag is
pushed. The workflow lives in `.github/workflows/release.yml`. The
tag is the trigger — it is **not** the source of version metadata.
Assemblies stamp from `Directory.Build.props`, which must be bumped
before tagging.

### Cutting a release

1. Edit `Directory.Build.props` per the "Bumping build version"
   section above (PATCH or MINOR rules).
2. Edit `CHANGELOG.md`: add a `## [X.Y.Z] - YYYY-MM-DD` heading
   below `## [Unreleased]`, move accumulated unreleased entries
   into it. Call out any protocol or contracts bumps under
   "Changed".
3. If protocol or contracts versions changed, edit
   `NodeApiContract.vb` to match. Update the corresponding history
   table in this document.
4. Commit: `Release X.Y.Z`.
5. Tag: `git tag vX.Y.Z`.
6. Push the tag: `git push origin vX.Y.Z`.

The push triggers `release.yml`, which runs three jobs:

- **build-windows** — publishes Manager (single-file self-contained,
  win-x64) and Node + NodeSetup (win-x64) on a Windows runner.
  Manager has to build on Windows because of WinForms; Node ships
  here too because its publish bundles the win-x64 self-contained
  CtrlCSender helper.
- **build-linux** — publishes Node + NodeSetup (linux-x64) on a
  Linux runner. No Manager on Linux — WinForms is Windows-only.
- **release** — downloads both artifact sets, extracts the
  matching `## [X.Y.Z]` section from `CHANGELOG.md`, creates the
  GitHub Release with that section as the body, and uploads three
  zips: `PowerGSM-Manager-X.Y.Z-win-x64.zip`,
  `PowerGSM-Node-X.Y.Z-win-x64.zip`,
  `PowerGSM-Node-X.Y.Z-linux-x64.zip`.

If the changelog section is missing, the release job fails with a
clear error rather than producing a release with empty notes.

Tags containing a hyphen (`v0.2.0-rc1`, `v0.2.0-beta`) are flagged
as pre-releases on the GitHub Releases page. Stable releases are
plain `vMAJOR.MINOR.PATCH`.

### Untagging is forbidden

If an artifact is wrong, do **not** delete and re-tag the same
version. Bump PATCH and tag a new release. Re-tagging the same
version produces two different binaries that claim to be the same
release, which is exactly the failure mode this whole document
exists to prevent.

### CI workflow

`.github/workflows/ci.yml` runs on every push to `master` and on
every PR. It builds `PowerGSM.sln` in Release configuration on a
Windows runner. It does not produce artifacts — just verifies the
solution compiles. Linux build coverage happens during release
(the `build-linux` job in `release.yml`).
