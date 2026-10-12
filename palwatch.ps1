# palwatch.ps1 - Palworld REST API watcher for diagnosing disconnects
# Requires in PalWorldSettings.ini: RESTAPIEnabled=True, AdminPassword set
# Run from any machine that can reach the server:
#   powershell -ExecutionPolicy Bypass -File .\palwatch.ps1 -Server 192.168.1.50 -AdminPassword "yourpass" -Port 33052
# One-shot ghost-session clear (no server restart):
#   powershell -ExecutionPolicy Bypass -File .\palwatch.ps1 -Server 192.168.1.50 -AdminPassword "yourpass" -Port 33052 -KickUserId "steam_7656..."

param(
    [Parameter(Mandatory=$true)][string]$Server,
    [Parameter(Mandatory=$true)][string]$AdminPassword,
    [int]$Port = 33052,
    [int]$IntervalSec = 5,
    [string]$OutFile = "palwatch.log",
    [string]$KickUserId = ""
)

$base = "http://${Server}:${Port}/v1/api"
$pair = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:$AdminPassword"))
$headers = @{ Authorization = "Basic $pair"; Accept = "application/json" }

function Log($msg) {
    $line = "{0:yyyy-MM-dd HH:mm:ss.fff}  {1}" -f (Get-Date), $msg
    Write-Host $line
    Add-Content -Path $OutFile -Value $line
}

function Api($path, $method = "GET", $body = $null) {
    $args = @{ Uri = "$base$path"; Method = $method; Headers = $headers; TimeoutSec = 5 }
    if ($body) { $args.Body = ($body | ConvertTo-Json -Compress); $args.ContentType = "application/json" }
    return Invoke-RestMethod @args
}

# --- One-shot kick mode ---
if ($KickUserId -ne "") {
    try {
        Api "/kick" "POST" @{ userid = $KickUserId; message = "clearing ghost session" } | Out-Null
        Log "KICK sent for $KickUserId - try rejoining now"
    } catch { Log "KICK FAILED: $($_.Exception.Message)" }
    exit
}

# --- Watch mode ---
Log "=== palwatch start: $base every ${IntervalSec}s ==="
$lastUptime = -1
$lastPlayers = ""
$lastApiOk = $true
$lastPingOk = $true

while ($true) {
    # ICMP alongside API: separates 'network path dead' from 'API/server dead'
    $pingOk = Test-Connection -ComputerName $Server -Count 1 -Quiet -ErrorAction SilentlyContinue
    if ($pingOk -ne $lastPingOk) {
        Log ("PING " + $(if ($pingOk) { "RESTORED" } else { "LOST - host unreachable at ICMP level" }))
        $lastPingOk = $pingOk
    }

    try {
        $m = Api "/metrics"
        $p = Api "/players"

        if (-not $lastApiOk) { Log "API RESTORED"; $lastApiOk = $true }

        # uptime going backwards = server process restarted
        if ($lastUptime -ge 0 -and $m.uptime -lt $lastUptime) {
            Log "!! UPTIME RESET ($lastUptime -> $($m.uptime)) - server restarted"
        }
        $lastUptime = $m.uptime

        $plist = ($p.players | ForEach-Object { "$($_.name)[ping=$([math]::Round($_.ping,1))ms userId=$($_.userId)]" }) -join ", "
        if ($plist -ne $lastPlayers) {
            Log "PLAYERS CHANGED: [$plist]"
            $lastPlayers = $plist
        }

        Log ("fps={0} frametime={1}ms players={2}/{3} uptime={4}s | {5}" -f `
            $m.serverfps, [math]::Round($m.serverframetime,1), $m.currentplayernum, $m.maxplayernum, $m.uptime, $plist)
    }
    catch {
        if ($lastApiOk) { Log "API LOST: $($_.Exception.Message)"; $lastApiOk = $false }
        else { Log "api still down: $($_.Exception.Message)" }
    }

    Start-Sleep -Seconds $IntervalSec
}
