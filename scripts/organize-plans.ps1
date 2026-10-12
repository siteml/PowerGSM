# organize-plans.ps1 - move all *_Plan.md into docs\plans\ and fix references.
# Run from the repo root (or via scripts\organize-plans.bat).
# Uses git mv so history follows the files. Living docs (ROADMAP, Backlog,
# CHANGELOG, README, VERSIONING, RELEASE_PROCESS, PlannedPlugins) stay at root.
# Intra-plan bare-filename references keep working because every plan lands in
# the same folder; only root docs get their links rewritten to docs/plans/.

$ErrorActionPreference = "Stop"

if (-not (Test-Path ".git")) { Write-Error "Run this from the repo root."; exit 1 }

$dest = "docs\plans"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$root = (Get-Location).Path
$plans = Get-ChildItem -File -Filter "*_Plan.md" | Where-Object { $_.DirectoryName -eq $root }
if (-not $plans -or $plans.Count -eq 0) { Write-Host "No *_Plan.md files at root - nothing to do."; exit 0 }

foreach ($p in $plans) {
    Write-Host ("moving {0} -> {1}\" -f $p.Name, $dest)
    git mv "$($p.Name)" "$dest\$($p.Name)"
    if ($LASTEXITCODE -ne 0) {
        Write-Warning ("git mv failed for {0} (untracked?) - plain move instead" -f $p.Name)
        Move-Item "$($p.Name)" "$dest\$($p.Name)"
    }
}

# Rewrite references in root living docs: bare Foo_Plan.md -> docs/plans/Foo_Plan.md
# (skips occurrences already carrying a path)
$livingDocs = @("ROADMAP.md","Backlog.md","CHANGELOG.md","README.md","PlannedPlugins.md","VERSIONING.md","RELEASE_PROCESS.md") |
    Where-Object { Test-Path $_ }

foreach ($doc in $livingDocs) {
    $text = Get-Content -Raw -Encoding UTF8 $doc
    $updated = [regex]::Replace($text, '(?<![\w/\\])([A-Za-z0-9][A-Za-z0-9-]*_Plan\.md)', 'docs/plans/$1')
    if ($updated -ne $text) {
        Set-Content -Encoding UTF8 -NoNewline -Path $doc -Value $updated
        Write-Host ("updated references in {0}" -f $doc)
    }
}

Write-Host ""
Write-Host "Done. Review with: git status; git diff --stat"
Write-Host "Then commit, e.g.: git commit -am ""docs: move plan documents to docs/plans"""

Write-Host ""
Write-Host "Leftover bare references outside docs\plans (review manually):"
$leftovers = git grep -n "_Plan\.md" -- . ":(exclude)docs/plans" 2>$null | Where-Object { $_ -notmatch "docs/plans/" }
if ($leftovers) { $leftovers | ForEach-Object { Write-Host ("  {0}" -f $_) } } else { Write-Host "  (none)" }
