<#
.SYNOPSIS
  Bump the build version in Directory.Build.props.

.DESCRIPTION
  Updates <Version>, <FileVersion>, and (on minor or major bumps)
  <AssemblyVersion> in the solution-root Directory.Build.props.
  Does NOT touch CHANGELOG.md, NodeApiContract.vb, git, or anything
  else — those are intentional manual steps because their content
  matters more than the heading.

  Validates the new version string against pre-1.0 SemVer:
  MAJOR.MINOR.PATCH, optionally followed by a hyphen and an
  alphanumeric pre-release tag (rc1, beta, alpha.2, etc.).

  Detects whether the bump is patch / minor / major by comparing
  against the current <Version> in props, and follows the .NET
  convention of bumping AssemblyVersion only on minor or major
  (PATCH-only releases keep AssemblyVersion stable).

  After updating the file, prints a checklist of the manual
  follow-up steps (CHANGELOG, protocol/contracts version review,
  commit, tag, push) so you don't forget any.

.PARAMETER NewVersion
  The new version string. Must match ^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$.

.EXAMPLE
  .\scripts\bump-version.ps1 0.1.1
  PATCH bump: <Version> and <FileVersion> updated. AssemblyVersion
  unchanged.

.EXAMPLE
  .\scripts\bump-version.ps1 0.2.0
  MINOR bump: <Version>, <FileVersion>, and <AssemblyVersion>
  all updated. Reminder printed about reviewing protocol /
  contracts versions.

.EXAMPLE
  .\scripts\bump-version.ps1 0.2.0-rc1
  Pre-release. Treated as a MINOR bump for AssemblyVersion
  purposes (the underlying base version is 0.2.0). FileVersion
  carries 0.2.0.0 (no pre-release tag — FileVersion is numeric
  only). <Version> carries the full 0.2.0-rc1.

.NOTES
  PowerShell execution policy may block this script. Run with:
    powershell -ExecutionPolicy Bypass -File .\scripts\bump-version.ps1 0.1.1
  Or set the policy once:
    Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
#>

param(
    [Parameter(Mandatory, Position = 0)]
    [string]$NewVersion
)

$ErrorActionPreference = 'Stop'

# ---- Validation -----------------------------------------------------------

# Pre-1.0 SemVer format. The pre-release suffix is optional and may contain
# alphanumerics and dots (rc1, beta, alpha.2, etc.) but no plus signs (build
# metadata) — we don't use those.
if ($NewVersion -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') {
    Write-Error @"
Invalid version format: '$NewVersion'

Expected: MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-tag

Examples:
  0.1.0          stable patch/minor/major release
  0.2.0-rc1      release candidate
  0.2.0-beta     beta
  0.2.0-alpha.2  alpha increment
"@
    exit 1
}

# Strip any pre-release suffix so we can do integer comparisons against
# the current props version. AssemblyVersion math always uses the base
# numeric version.
$baseParts = ($NewVersion -split '-')[0] -split '\.'
$newMajor = [int]$baseParts[0]
$newMinor = [int]$baseParts[1]
$newPatch = [int]$baseParts[2]

# ---- Locate Directory.Build.props -----------------------------------------

# Resolve relative to the script's own folder so the script works when run
# from any cwd (the user's habit is to invoke from the solution root, but
# someone running it from inside scripts\ shouldn't get a confusing error).
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$propsFile = Join-Path $repoRoot 'Directory.Build.props'

if (-not (Test-Path $propsFile)) {
    Write-Error "Directory.Build.props not found at expected location: $propsFile"
    exit 1
}

# Read whole file as one string. Line endings are preserved as-is when we
# write it back via [System.IO.File]::WriteAllText so git's autocrlf
# behaviour stays in charge of normalisation.
$content = Get-Content $propsFile -Raw

# ---- Read current version & detect bump kind ------------------------------

if (-not ($content -match '<Version>([^<]+)</Version>')) {
    Write-Error "Could not find <Version> tag in $propsFile"
    exit 1
}
$currentVersion = $matches[1].Trim()

$currentBaseParts = ($currentVersion -split '-')[0] -split '\.'
$curMajor = [int]$currentBaseParts[0]
$curMinor = [int]$currentBaseParts[1]
$curPatch = [int]$currentBaseParts[2]

# Bump kind drives the AssemblyVersion-update decision below.
$bumpKind =
    if ($newMajor -ne $curMajor) { 'major' }
    elseif ($newMinor -ne $curMinor) { 'minor' }
    elseif ($newPatch -ne $curPatch) { 'patch' }
    else { 'pre-release' }    # base version unchanged, only suffix differs

# ---- Update the file ------------------------------------------------------

# AssemblyVersion follows the convention MAJOR.MINOR.0.0. We don't sign
# assemblies so it doesn't strictly matter, but the convention costs
# nothing and keeps PATCH-only bumps from changing assembly identity.
$newAssemblyVersion = "$newMajor.$newMinor.0.0"

# FileVersion carries MAJOR.MINOR.PATCH.0. The trailing .0 is the .NET
# convention for "build number unspecified". Pre-release tags don't
# appear here — FileVersion is numeric-only by Win32 file-resource
# spec, so 0.2.0-rc1 still maps to FileVersion 0.2.0.0.
$newFileVersion = "$newMajor.$newMinor.$newPatch.0"

# Always update Version and FileVersion. AssemblyVersion only on
# minor/major (or major-via-pre-release).
$content = $content -replace '<Version>[^<]+</Version>', "<Version>$NewVersion</Version>"
$content = $content -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$newFileVersion</FileVersion>"

$assemblyVersionChanged = $false
if ($bumpKind -in @('minor', 'major')) {
    $content = $content -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$newAssemblyVersion</AssemblyVersion>"
    $assemblyVersionChanged = $true
}

# Write atomically. WriteAllText preserves the file's existing line endings
# and does not append a trailing newline — matches what most editors do
# when the source file already has the right shape.
[System.IO.File]::WriteAllText($propsFile, $content)

# ---- Summary --------------------------------------------------------------

Write-Host ""
Write-Host "Version bumped: $currentVersion -> $NewVersion ($bumpKind)" -ForegroundColor Green
Write-Host ""
Write-Host "Directory.Build.props updated:" -ForegroundColor Cyan
Write-Host "  <Version>             $NewVersion"
Write-Host "  <FileVersion>         $newFileVersion"
if ($assemblyVersionChanged) {
    Write-Host "  <AssemblyVersion>     $newAssemblyVersion  (bumped: $bumpKind release)"
} else {
    Write-Host "  <AssemblyVersion>     unchanged ($bumpKind release keeps assembly identity)"
}

Write-Host ""
Write-Host "Manual follow-up:" -ForegroundColor Yellow
$step = 1
$today = Get-Date -Format 'yyyy-MM-dd'
Write-Host "  $step. Edit CHANGELOG.md: add a '## [$NewVersion] - $today' section below '## [Unreleased]'"
$step++
if ($bumpKind -in @('minor', 'major')) {
    Write-Host "  $step. Decide whether NodeApiContract.ProtocolVersion or ContractsVersion need bumping"
    Write-Host "     (see VERSIONING.md for the policy on each). Update the integers and the"
    Write-Host "     corresponding history table in VERSIONING.md if so."
    $step++
}
Write-Host "  $step. Commit:  git commit -am ""Release $NewVersion"""
$step++
Write-Host "  $step. Tag:     git tag v$NewVersion"
$step++
Write-Host "  $step. Push:    git push origin master v$NewVersion"
$step++
Write-Host "  $step. Watch the Release workflow in the GitHub Actions tab"
Write-Host ""
Write-Host "See RELEASE_PROCESS.md for the full procedure including troubleshooting." -ForegroundColor DarkGray
Write-Host ""
