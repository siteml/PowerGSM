# Release Process

How to cut a PowerGSM release. Read [VERSIONING.md](VERSIONING.md) first
to understand the version-number policy and what each axis means; this
document is the procedural counterpart that explains *how* to ship.

The whole pipeline is automated by `.github/workflows/release.yml`,
triggered by pushing a `v*.*.*` tag. The work this document captures is
what happens *around* that automation — the local edits, the verification
steps, and the troubleshooting paths when something doesn't behave.

---

## Quick reference

The happy path, in six steps:

1. `.\scripts\bump-version.ps1 X.Y.Z` (or edit `Directory.Build.props` by hand)
2. Edit `CHANGELOG.md`: add `## [X.Y.Z] - YYYY-MM-DD` section below `## [Unreleased]`
3. If MINOR / MAJOR bump, decide whether `NodeApiContract.ProtocolVersion` or `ContractsVersion` needs bumping; update the corresponding history table in `VERSIONING.md`
4. Commit: `git commit -am "Release X.Y.Z"`
5. Tag and push: `git tag vX.Y.Z && git push origin master vX.Y.Z`
6. Watch the Actions tab; verify the Release page shows three zips with correct names

If anything goes sideways, see [Troubleshooting](#troubleshooting) below.

---

## Cutting a stable release

### 1. Bump the version

The script handles the props file:

```
.\scripts\bump-version.ps1 0.1.1   # patch bump
.\scripts\bump-version.ps1 0.2.0   # minor bump (also updates AssemblyVersion)
```

It updates `<Version>` and `<FileVersion>` always, and `<AssemblyVersion>`
only on MINOR or MAJOR. The convention is `MAJOR.MINOR.0.0` for
AssemblyVersion so PATCH-only bumps don't change assembly identity.

If you'd rather edit by hand, the rules are documented in
[VERSIONING.md](VERSIONING.md#bumping-build-version).

### 2. Update CHANGELOG.md

Add a section below `## [Unreleased]`:

```markdown
## [Unreleased]

## [0.2.0] - 2026-05-15

### Added

- Feature description.

### Changed

- Behavioural change description. Breaking changes go here, prominently.

### Fixed

- Bug fix description.
```

If protocol or contracts versions changed, mention that explicitly under
`Changed`. Use the section names from
[Keep-a-Changelog](https://keepachangelog.com/) (Added / Changed /
Deprecated / Removed / Fixed / Security) — the workflow doesn't care, but
human readers look for the conventional buckets.

The release workflow extracts everything between `## [X.Y.Z]` and the
next `## [` to use as the GitHub Release body. Anything you write in
the section appears verbatim on the release page.

### 3. Review protocol and contracts versions

PATCH bumps almost never touch these. MINOR bumps sometimes do. If
either changed:

1. Edit `GSM.Contracts/NodeApiContract.vb` to bump the relevant
   `Public Const` integer.
2. Add a row to the matching history table in `VERSIONING.md`
   (Protocol history or Contracts history).
3. Mention the bump in the CHANGELOG section under `Changed`.

If neither changed, skip this step.

### 4. Commit

```
git commit -am "Release 0.2.0"
```

The commit message is conventional but not load-bearing — the tag is what
the workflow keys on, not the message.

### 5. Tag and push

```
git tag v0.2.0
git push origin master v0.2.0
```

The `v` prefix matters — the workflow's tag pattern is `v*.*.*`. A bare
`0.2.0` tag will not trigger anything.

If you prefer Visual Studio's UI: Git Repository window → right-click on
the just-committed commit → Create Tag → name it `v0.2.0`. Then in the
left tree, find the new tag under `tags`, right-click it, Push Tag. VS's
regular Sync command pushes branches but **not** tags — this is a
longstanding Git gotcha that catches everyone once.

### 6. Verify

Within ~15 seconds of the tag landing on GitHub, the Actions tab should
show a new "Release" workflow run with three jobs:

- `Build Windows artifacts` (~5–10 min)
- `Build Linux artifacts` (~3–5 min, in parallel)
- `Create GitHub Release` (~30 sec, after both builds finish)

Once green, head to the Releases page (`/releases`) and confirm:

- Title reads `PowerGSM 0.2.0`
- Body is the matching CHANGELOG section (Added / Changed / Fixed)
- Three zips attached:
  - `PowerGSM-Manager-0.2.0-win-x64.zip`
  - `PowerGSM-Node-0.2.0-win-x64.zip`
  - `PowerGSM-Node-0.2.0-linux-x64.zip`
- Pre-release flag NOT set (it's only auto-set on tags containing a hyphen)

For extra confidence, download one zip, extract somewhere fresh, run the
binary. The Manager's About dialog should show `0.2.0`, the Node's
startup log should say `GSM.Node 0.2.0 starting`.

---

## Pre-release dry runs (rc tags)

Use a pre-release tag (`v0.2.0-rc1`, `v0.2.0-beta`, etc.) when you want
to exercise the release pipeline without committing to a stable version.
The workflow auto-flags any tag containing a hyphen as a pre-release on
the GitHub Releases page.

Useful for:

- Testing changes to `release.yml` itself
- Validating a release on a fresh runner before announcing
- Coordinating with downstream users who want to test before the
  stable cut

The dance:

1. Add a `## [0.2.0-rc1]` section to CHANGELOG (brief — it's a dry run,
   doesn't need full release notes)
2. Commit, push the branch
3. `git tag v0.2.0-rc1 && git push origin v0.2.0-rc1`
4. Watch Actions; if anything fails, fix it, commit
5. **Do not reuse the rc1 tag.** Bump to `rc2`:
   - `git tag -d v0.1.0-rc1` (delete local)
   - `git tag v0.1.0-rc2`
   - Update CHANGELOG section heading from `rc1` to `rc2`
   - Commit + push, push the new tag

Once the rc passes, cut the real release: delete the rc CHANGELOG stub
(or keep it as historical record — your call), tag the stable version,
push.

To clean up rc tags from GitHub afterwards (cosmetic — they're harmless
left around):

```
git push origin --delete v0.1.0-rc1 v0.1.0-rc2 v0.1.0-rc3
```

Or via the GitHub UI: Releases page → trash icon on each rc entry.

---

## Troubleshooting

Failure modes seen during the 0.1.0 release rcs, kept here so future-you
recognises them faster.

### The tag was created but no workflow ran

Most common cause: the tag is local-only and never reached GitHub.
VS's Sync command pushes branches but not tags. To verify:

- GitHub repo → Code → Tags tab. Should list the tag if it was pushed.
- If absent: in VS Git Repository window, right-click the tag in the
  tags tree → Push Tag. Or `git push origin vX.Y.Z` from CLI.

Other possible cause: the tag pattern is wrong. The workflow only fires
on `v*.*.*` tags. A tag like `release-0.2.0` or `0.2.0` (no `v` prefix)
won't trigger anything.

### Workflow ran but the build job failed

Click into the failed job, expand the red ✗ step, read the last 30–50
lines of its log. The annotations summary at the top of the run page
sometimes truncates the actual error.

Specific failure modes seen during 0.1.0:

**`NETSDK1198: A publish profile with the name 'X' was not found`**

VB.NET projects keep their pubxml files under `My Project\PublishProfiles\`,
but the .NET SDK's `PublishProfile` lookup hardcodes `Properties\PublishProfiles\`.
Visual Studio passes the full path explicitly so VS publish works fine, but
CLI publish using `-p:PublishProfile=name` falls back to framework defaults
with a warning (build "succeeds" with the wrong shape — no `SelfContained`,
no `PublishSingleFile`, etc.).

Fix: use `-p:PublishProfileFullPath="path/to/profile.pubxml"` instead of
`-p:PublishProfile=name`. The workflow already does this for all five
publish steps. If you're seeing this fresh, the workflow probably had a
new publish step added without the FullPath form.

**`NETSDK1129: The 'Publish' target is not supported without specifying a target framework`**

A multi-targeted project (`net8.0;net8.0-windows`) needs an explicit
`-f` argument on `dotnet publish`. The pubxml's `<TargetFramework>` is
read too late — the SDK's CrossTargeting check fires first.

Affected projects: `GSM.NodeSetup` (multi-TFM by design — net8.0 for
Linux console build, net8.0-windows for Windows GUI build).

Fix: pass `-f net8.0-windows` (Windows publish) or `-f net8.0` (Linux
publish) on the command line.

**`NETSDK1100: To build a project targeting Windows on this operating system, set the EnableWindowsTargeting property to true`**

Two distinct cases here:

1. **At restore time on a Linux runner:** `dotnet restore` resolves all
   TFMs of a multi-TFM project regardless of `--framework` (because
   restore doesn't honour --framework). The Windows-slot resolution
   trips the check. Fix: `-p:EnableWindowsTargeting=true` on the
   restore command.

2. **At publish time, inside an inner MSBuild call:** Node's vbproj has
   a `<Target Name="PublishCtrlCSender" BeforeTargets="Publish">` that
   invokes `<MSBuild Projects="..." Properties="...">` to cross-compile
   a win-x64 self-contained CtrlCSender helper. The `<MSBuild>` task's
   `Properties` attribute *replaces* the global property set rather
   than appending — so `EnableWindowsTargeting=true` set on the outer
   `dotnet publish` does NOT propagate. Fix: add the property to the
   inner Properties string in `GSM.Node.vbproj` directly.

(Self-contained publish targeting a `win-*` RID from a Linux host
triggers this check even when the target project's TFM is plain `net8.0`.
The check fires on the RID + SelfContained combination, not the TFM.)

**`The path '...' either does not exist or is not a valid file system path` (zip step)**

The publish landed somewhere different from where the zip step looked.
Most likely the pubxml's `<PublishDir>` (or older `<PublishUrl>`) was
ignored by the SDK in favour of the RID-aware default path
`bin\Configuration\TFM\RID\publish\` (RID before "publish").

Fix: use `-p:PublishDir=publish/some-explicit-path/` on the publish
command, and zip from that explicit path. The workflow already does
this for Manager (Windows) and Node (Linux). If a new project gets
added to the workflow, follow the same pattern.

If the project has downstream targets that depend on its publish
location (e.g. NodeSetup's `DeployToNodeFolder` target copies into
Node's publish folder), also override `NodeDeploymentDir` on the
NodeSetup publish to match.

### Workflow succeeded but release notes are empty / wrong

The workflow extracts the section under `## [X.Y.Z]` from CHANGELOG.md.
Empty or wrong notes usually mean:

- The CHANGELOG section is named differently (typo in the version
  number, missing brackets, etc.). The awk pattern matches `$2 == "[X.Y.Z]"`
  exactly.
- The section exists but has no body content.
- The CHANGELOG hadn't been updated when the tag was pushed.

The workflow's "Extract changelog section" step has an `if [ -z "$notes" ]`
check that errors out loudly if it can't find content — so a missing
section fails the release job rather than producing a blank release.
But typos in the heading slip through this check (it just doesn't
match anything, then errors).

### Release was created but artifacts are missing

The Release job's `softprops/action-gh-release@v2` upload uses
`fail_on_unmatched_files: true`, so missing artifacts fail the job
rather than silently producing an incomplete release. If you have a
release with missing artifacts, it's likely an old release from before
that flag was added — delete and re-cut.

If only one or two zips are missing on a fresh release:

- The matching upload-artifact step might have been skipped (check
  the build job's outcome — a build job failure normally fails the
  whole pipeline before release runs, but partial-success edge cases
  can occur if `if:` conditions in the workflow are wrong)
- The `merge-multiple: true` on `download-artifact` requires unique
  filenames across all artifact sets. If two upload steps produce the
  same filename, one overwrites the other. The current workflow uses
  `-win-x64` vs `-linux-x64` in the filenames so this is fine, but
  worth checking if you add new artifacts.

---

## Hotfixes

If a release artifact is broken (the build was fine but the result
doesn't actually work), **do not delete and re-tag the same version.**
Bump PATCH and tag a new release. Re-tagging the same version produces
two different binaries that claim to be the same release, which is the
exact failure mode this whole versioning story exists to prevent.

Hotfix flow:

1. Fix the bug.
2. `.\scripts\bump-version.ps1 0.2.1` (or whatever the next PATCH is)
3. CHANGELOG entry under `## [0.2.1]` calling out what was fixed.
4. Commit, tag `v0.2.1`, push.

Users running the broken 0.2.0 will see the new version on the Releases
page; if/when an auto-update check is built (Phase 5f-5 deferred this
to v2), they'll be notified directly.

---

## Yanking a release

Rare, but: if a release shipped that should NEVER be installed (security
issue, data corruption bug, etc.), you can mark it as broken:

1. Edit the GitHub Release entry → check "Set as a pre-release" or just
   add a prominent ⚠️ warning at the top of the body.
2. Add a follow-up entry to CHANGELOG.md (in the next release's
   section) with a "Removed" or "Security" note explaining the yank.
3. Cut the next PATCH or MINOR with the fix.

Don't actually delete the GitHub Release entry or the git tag — anyone
who cloned the repo or downloaded the artifact between the release and
the yank will have stale references that 404 awkwardly. Better to leave
the broken release in place with a warning and ship the fix as a new
version.
