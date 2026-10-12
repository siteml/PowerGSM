# =============================================================
#  eventstream-gates.ps1 - Slice 1 verification gates Q1/Q2/Q3
#  (EventStream Unification plan, docs/plans/)
#
#  ASCII-only and Windows PowerShell 5.1 compatible on purpose:
#  PS 5.1 reads BOM-less files as ANSI, and UTF-8 punctuation
#  (em-dashes etc.) decodes into cp1252 curly quotes that PS
#  treats as string terminators -> parse errors. Keep it ASCII.
#
#    Q1  (seq continuity across node restart):
#        .\eventstream-gates.ps1 -Gate Q1 -Node http://127.0.0.1:8765 -Token <t> -InstanceId <id>
#        Captures the stream, waits for you to restart the node
#        service (games keep running under their shims), then
#        re-reads and asserts: no duplicate seqs, no gaps among
#        NEW events, cursor picks up exactly where it left off.
#
#    Q2  (wire-compat of /players - run BEFORE deploying with
#         -Baseline, then AFTER with -Compare, on LO + Windrose):
#        .\eventstream-gates.ps1 -Gate Q2 -Node ... -Token ... -InstanceId <id> -Baseline
#        .\eventstream-gates.ps1 -Gate Q2 -Node ... -Token ... -InstanceId <id> -Compare
#        Byte-compares the raw response body against the saved
#        baseline file (q2-baseline-<id>.json beside this script).
#        NOTE: /players content shifts as players join/leave -
#        run the pair back-to-back on a quiet instance, or accept
#        a field-shape comparison via -ShapeOnly.
#
#    Q3  (crash lifecycle in stream + fold): crash the test
#        instance's process (kill the game exe), let the node
#        crash-restart it, then:
#        .\eventstream-gates.ps1 -Gate Q3 -Node ... -Token ... -InstanceId <id>
#        Asserts ProcessExitedUnexpectedly + CrashRestartScheduled
#        + ProcessStarted are in the stream, then folds the
#        lifecycle events client-side and asserts the fold lands
#        on Running with CrashCount >= 1.
# =============================================================
param(
    [Parameter(Mandatory)][ValidateSet("Q1","Q2","Q3")] [string]$Gate,
    [Parameter(Mandatory)][string]$Node,
    [Parameter(Mandatory)][string]$Token,
    [Parameter(Mandatory)][string]$InstanceId,
    [switch]$Baseline,
    [switch]$Compare,
    [switch]$ShapeOnly
)
$ErrorActionPreference = "Stop"
$H = @{ Authorization = "Bearer $Token" }
# Lifecycle kind integers (ParsedEventKind, append-only)
$K = @{ ProcessStarted=7; ProcessExited=8; ProcessExitedUnexpectedly=9;
        CrashRestartScheduled=10; CrashLoopHalted=11; InstanceStopRequested=12; InstanceAdopted=13 }
$KName = @{}
foreach ($pair in $K.GetEnumerator()) { $KName[$pair.Value] = $pair.Key }

function Get-Events([long]$since, [int]$max = 5000) {
    (Invoke-RestMethod -Headers $H -Uri "$Node/api/instances/$InstanceId/events?since=$since&max=$max").events
}
function Get-AllEvents {
    $all = @(); $cur = 0
    while ($true) {
        $b = @(Get-Events $cur)
        if (-not $b -or $b.Count -eq 0) { break }
        $all += $b
        $cur = $b[$b.Count - 1].seq
    }
    return ,$all
}
function Fail($msg) { Write-Host "GATE $Gate FAIL: $msg" -ForegroundColor Red; exit 1 }
function Pass($msg) { Write-Host "GATE $Gate PASS: $msg" -ForegroundColor Green; exit 0 }

switch ($Gate) {
  "Q1" {
    $before = Get-AllEvents
    if ($before.Count -eq 0) { Fail "no events in stream - start the instance and generate some activity first" }
    $lastSeq = [long]$before[$before.Count - 1].seq
    Write-Host "Captured $($before.Count) event(s); last seq = $lastSeq"
    Read-Host "Restart the node service now - sc stop GSMNode then sc start GSMNode - wait for adoption, generate a little more activity in-game, then press Enter" | Out-Null
    $after = Get-AllEvents
    $seqs = @($after | ForEach-Object { [long]$_.seq })
    $dupes = @($seqs | Group-Object | Where-Object { $_.Count -gt 1 })
    if ($dupes.Count -gt 0) { Fail ("duplicate seq(s): " + (($dupes | ForEach-Object { $_.Name }) -join ", ")) }
    $newSeqs = @($seqs | Where-Object { $_ -gt $lastSeq } | Sort-Object)
    if ($newSeqs.Count -eq 0) { Fail "no new events after restart - did adoption succeed and did you generate activity?" }
    for ($i = 1; $i -lt $newSeqs.Count; $i++) {
        if ($newSeqs[$i] -ne ($newSeqs[$i-1] + 1)) { Fail "gap in post-restart seqs between $($newSeqs[$i-1]) and $($newSeqs[$i])" }
    }
    if ($newSeqs[0] -ne ($lastSeq + 1)) { Fail "post-restart seq started at $($newSeqs[0]), expected $($lastSeq + 1)" }
    # pre-restart prefix must be unchanged (replay dedup appended nothing new below the old cursor)
    $beforeIds = ($before | ForEach-Object { "$($_.seq):$($_.kind)" }) -join "|"
    $afterPrefix = (@($after | Where-Object { [long]$_.seq -le $lastSeq }) | ForEach-Object { "$($_.seq):$($_.kind)" }) -join "|"
    if ($beforeIds -ne $afterPrefix) { Fail "pre-restart events changed across restart (replay dedup broken?)" }
    Pass "seq continued $lastSeq -> $($newSeqs[0])..$($newSeqs[$newSeqs.Count-1]), no dupes, no gaps, prefix stable"
  }
  "Q2" {
    $file = Join-Path $PSScriptRoot "q2-baseline-$InstanceId.json"
    $resp = Invoke-WebRequest -Headers $H -Uri "$Node/api/instances/$InstanceId/players" -UseBasicParsing
    $body = $resp.Content
    if ($Baseline) {
        Set-Content -Path $file -Value $body -NoNewline -Encoding UTF8
        Write-Host "Baseline saved to $file ($($body.Length) chars). Deploy the Slice-1 node, then re-run with -Compare."
        exit 0
    }
    if (-not $Compare) { Fail "specify -Baseline (pre-deploy) or -Compare (post-deploy)" }
    if (-not (Test-Path $file)) { Fail "no baseline file $file - run with -Baseline on the pre-slice binary first" }
    $base = Get-Content -Raw -Path $file
    if ($ShapeOnly) {
        function Get-Shape($json) {
            $objs = @($json | ConvertFrom-Json)
            @($objs | ForEach-Object { ($_.PSObject.Properties.Name | Sort-Object) -join "," } | Select-Object -Unique)
        }
        $a = Get-Shape $base
        $b = Get-Shape $body
        if (Compare-Object $a $b) { Fail "field shape differs: baseline [$($a -join ' | ')] vs current [$($b -join ' | ')]" }
        Pass "/players field shape identical (content comparison skipped by -ShapeOnly)"
    }
    if ($base -ne $body) {
        Write-Host "--- baseline ---"
        Write-Host $base
        Write-Host "--- current ---"
        Write-Host $body
        Fail "/players body differs from baseline (if players joined/left between runs, re-run the pair back-to-back or use -ShapeOnly)"
    }
    Pass "/players byte-identical to pre-slice baseline"
  }
  "Q3" {
    $all = Get-AllEvents
    $life = @($all | Where-Object { $_.sourceId -eq "lifecycle" })
    if ($life.Count -eq 0) { Fail "no lifecycle events in stream" }
    Write-Host "Lifecycle events:"
    foreach ($e in $life) {
        $fjson = ""
        if ($e.fields) { $fjson = ($e.fields | ConvertTo-Json -Compress) }
        Write-Host ("{0,6}  {1,-26} {2}" -f $e.seq, $KName[[int]$e.kind], $fjson)
    }
    foreach ($need in @("ProcessExitedUnexpectedly","CrashRestartScheduled","ProcessStarted")) {
        $found = @($life | Where-Object { [int]$_.kind -eq $K[$need] })
        if ($found.Count -eq 0) { Fail "missing $need in stream" }
    }
    # client-side fold (mirrors EventStreamStore.FoldLifecycle)
    # NOTE: loop var must NOT be named $k - PowerShell variables are
    # case-insensitive, so $k would overwrite the $K kind table.
    $state = "Unknown"; $crashCount = 0
    foreach ($e in $life) {
        $ki = [int]$e.kind
        if     ($ki -eq $K.ProcessStarted)            { $state = "Running" }
        elseif ($ki -eq $K.ProcessExited)             { $state = "Stopped"; $crashCount = 0 }
        elseif ($ki -eq $K.ProcessExitedUnexpectedly) { $state = "Crashed" }
        elseif ($ki -eq $K.CrashRestartScheduled)     { $crashCount = [int]$e.fields.CrashCount }
        elseif ($ki -eq $K.CrashLoopHalted)           { $state = "CrashLoopHalted" }
        elseif ($ki -eq $K.InstanceAdopted)           { $state = "Running" }
    }
    Write-Host "Fold result: state=$state crashCount=$crashCount"
    if ($state -ne "Running") { Fail "fold state is $state, expected Running (is the instance back up?)" }
    if ($crashCount -lt 1) { Fail "fold CrashCount is $crashCount, expected >= 1" }
    # cross-check against /status
    $status = Invoke-RestMethod -Headers $H -Uri "$Node/api/instances/$InstanceId/status"
    Write-Host "/status: state=$($status.currentState) crashCount=$($status.crashCount)"
    Pass "lifecycle events present; fold = Running with CrashCount=$crashCount"
  }
}
