<#
.SYNOPSIS
  Measures how much CPU and memory SYSCOM uses on this PC: idle, and while a dashboard streams every sensor at
  1 s and at 500 ms.

.DESCRIPTION
  Connects to the local SYSCOM like a dashboard would (a WebSocket asking for every sensor), and reads the
  syscom.exe process's CPU time and memory before and after each phase. CPU is shown as a share of the whole PC
  (like Task Manager) and of one core. Run it as administrator on a PC where SYSCOM is installed; nothing is changed.

  It needs an API key (create a read key in the dashboard: Settings > API keys), or the password.

  A warm-up (-WarmUp, 30 s) runs first, so the numbers show SYSCOM's steady state rather than its start-up.

  -Compare measures other monitoring apps the same way, for a side-by-side table: give a process name for an app
  that's already running (HWiNFO64, MSIAfterburner, RTSSHooksLoader64 ...), or the path of an .exe, which the
  script starts for the measurement and closes afterwards. Those apps are measured after SYSCOM's phases, with no
  dashboard open, so they don't compete for the sensors while SYSCOM is measured.

.EXAMPLE
  pwsh build\benchmark.ps1 -Key syscom_...
.EXAMPLE
  pwsh build\benchmark.ps1 -Password (Get-Content "$env:ProgramData\SYSCOM\initial-password.txt") -Seconds 30 -Markdown
.EXAMPLE
  pwsh build\benchmark.ps1 -Key syscom_... -Compare HWiNFO64, 'C:\Tools\LibreHardwareMonitor\LibreHardwareMonitor.exe'
#>
[CmdletBinding()]
param(
    [string]$Key,
    [string]$Password,
    [int]$Port = 47810,
    [int]$Seconds = 60,
    [int]$WarmUp = 30,
    [string[]]$Compare = @(),
    [switch]$Markdown
)

$ErrorActionPreference = 'Stop'
$base = "http://127.0.0.1:$Port"
if (-not $Key) {
    if (-not $Password) { throw 'Pass -Key (a read key from the dashboard: Settings > API keys) or -Password.' }
    $token = (Invoke-RestMethod "$base/api/login" -Method Post -ContentType 'application/json' -Body (@{ password = $Password } | ConvertTo-Json)).token
    $Key = (Invoke-RestMethod "$base/api/keys" -Method Post -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' `
        -Body '{"name":"benchmark (temporary)","access":"read"}').secret
    $temporary = $true
}
$info = Invoke-RestMethod "$base/api/info"
$sensors = (Invoke-RestMethod "$base/api/values" -Headers @{ 'X-API-Key' = $Key }).values.PSObject.Properties.Name.Count

function Get-Server {
    $p = Get-Process syscom -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Split-Path $_.Path -Leaf) -eq 'syscom.exe' } | Select-Object -First 1
    if (-not $p) { throw 'syscom.exe is not running (or not visible: run as administrator).' }
    $p.Refresh()
    $p
}

function Measure-Phase([string]$name, [int]$interval, [int]$duration = $Seconds) {
    $ws = $null
    $frames = 0; $bytes = 0
    $none = [Threading.CancellationToken]::None
    if ($interval) {
        $ws = [System.Net.WebSockets.ClientWebSocket]::new()
        $ws.ConnectAsync([uri]"ws://127.0.0.1:$Port/ws?key=$Key", $none).Wait()
        $sub = [Text.Encoding]::UTF8.GetBytes((@{ type = 'subscribe'; interval = $interval } | ConvertTo-Json -Compress))
        $ws.SendAsync([ArraySegment[byte]]::new($sub), 'Text', $true, $none).Wait()
    }
    $buffer = [byte[]]::new(4MB)
    $pending = $null
    # Settle for a few seconds (first catalog, caches), then measure.
    $settle = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $settle) {
        if ($ws) {
            if (-not $pending) { $pending = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), $none) }
            if ($pending.Wait(100)) { $pending = $null }
        } else { Start-Sleep -Milliseconds 200 }
    }
    $p = Get-Server
    $cpu0 = $p.TotalProcessorTime
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $ws0 = @(); $priv = @()
    $nextSample = 0
    while ($t0.Elapsed.TotalSeconds -lt $duration) {
        if ($ws) {
            if (-not $pending) { $pending = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), $none) }
            if ($pending.Wait(100)) {
                $r = $pending.Result
                $bytes += $r.Count
                if ($r.EndOfMessage) { $frames++ }
                $pending = $null
            }
        } else { Start-Sleep -Milliseconds 200 }
        if ($t0.Elapsed.TotalSeconds -ge $nextSample) {
            $p.Refresh()
            $ws0 += $p.WorkingSet64; $priv += $p.PrivateMemorySize64
            $nextSample += 1
        }
    }
    $p.Refresh()
    $cpu = ($p.TotalProcessorTime - $cpu0).TotalSeconds
    $wall = $t0.Elapsed.TotalSeconds
    if ($ws) { try { $ws.CloseAsync('NormalClosure', 'done', $none).Wait(2000) | Out-Null } catch { } ; $ws.Dispose() }
    [pscustomobject]@{
        Phase = $name
        'CPU (whole PC)' = '{0:0.00} %' -f (100 * $cpu / $wall / [Environment]::ProcessorCount)
        'CPU (one core)' = '{0:0.0} %' -f (100 * $cpu / $wall)
        'Memory (working set)' = '{0:0} MB' -f (($ws0 | Measure-Object -Average).Average / 1MB)
        'Memory (private)' = '{0:0} MB' -f (($priv | Measure-Object -Average).Average / 1MB)
        'Sent to the dashboard' = if ($ws) { '{0:0.0} KB/s ({1:0.0} frames/s)' -f ($bytes / 1KB / $wall), ($frames / $wall) } else { '-' }
    }
}

# Another app's processes, measured like SYSCOM over one phase (summed when it runs several processes).
function Measure-App([string]$app) {
    $started = $null
    $name = $app
    if ($app -match '[\\/]' -or $app -like '*.exe') {
        if (-not (Test-Path $app)) { Write-Warning "$app not found"; return }
        $name = [IO.Path]::GetFileNameWithoutExtension($app)
        $started = Start-Process $app -PassThru
        Start-Sleep -Seconds 15   # let it start up and find its sensors
    }
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if (-not $procs) { Write-Warning "$name is not running"; return }
    $version = try { $procs[0].MainModule.FileVersionInfo.ProductVersion -replace '\+.*$', '' } catch { '' }
    $cpu0 = ($procs | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $wsSamples = @(); $privSamples = @()
    while ($t0.Elapsed.TotalSeconds -lt $Seconds) {
        $procs | ForEach-Object { $_.Refresh() }
        $wsSamples += ($procs | Measure-Object WorkingSet64 -Sum).Sum
        $privSamples += ($procs | Measure-Object PrivateMemorySize64 -Sum).Sum
        Start-Sleep -Seconds 1
    }
    $procs | ForEach-Object { $_.Refresh() }
    $cpu = ($procs | ForEach-Object { $_.TotalProcessorTime.TotalSeconds } | Measure-Object -Sum).Sum - $cpu0
    $wall = $t0.Elapsed.TotalSeconds
    if ($started) { $started.CloseMainWindow() | Out-Null; if (-not $started.WaitForExit(5000)) { Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue } }
    [pscustomobject]@{
        Phase = "$name $version (for comparison, its own window)".Replace('  ', ' ')
        'CPU (whole PC)' = '{0:0.00} %' -f (100 * $cpu / $wall / [Environment]::ProcessorCount)
        'CPU (one core)' = '{0:0.0} %' -f (100 * $cpu / $wall)
        'Memory (working set)' = '{0:0} MB' -f (($wsSamples | Measure-Object -Average).Average / 1MB)
        'Memory (private)' = '{0:0} MB' -f (($privSamples | Measure-Object -Average).Average / 1MB)
        'Sent to the dashboard' = '-'
    }
}

$cpuName = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
Write-Host "SYSCOM $($info.version) on $cpuName ($([Environment]::ProcessorCount) logical cores, $ram GB), $sensors sensors, $Seconds s per phase"
# A freshly started SYSCOM is still compiling its hot paths and filling caches; let that settle first, or the first
# phase measures start-up rather than the steady state.
if ($WarmUp -gt 0) {
    Write-Host "Warming up for $WarmUp s (a dashboard at 1 s)..."
    Measure-Phase 'warm-up' 1000 $WarmUp | Out-Null
}
Write-Host 'Measuring: idle (no dashboard open)...'
$rows = @(Measure-Phase 'Idle (no dashboard open, history every 2 s)' 0)
Write-Host 'Measuring: dashboard at 1 s...'
$rows += Measure-Phase "Dashboard open, every sensor every 1 s" 1000
Write-Host 'Measuring: dashboard at 500 ms...'
$rows += Measure-Phase "Dashboard open, every sensor every 500 ms" 500
foreach ($app in $Compare) {
    Write-Host "Measuring: $app..."
    $row = Measure-App $app
    if ($row) { $rows += $row }
}

if ($temporary) {
    $id = (Invoke-RestMethod "$base/api/keys" -Headers @{ Authorization = "Bearer $token" } | Where-Object name -eq 'benchmark (temporary)').id
    foreach ($k in $id) { Invoke-RestMethod "$base/api/keys/$k" -Method Delete -Headers @{ Authorization = "Bearer $token" } | Out-Null }
}

if ($Markdown) {
    $cols = $rows[0].PSObject.Properties.Name
    "SYSCOM $($info.version) · $cpuName ($([Environment]::ProcessorCount) logical cores, $ram GB RAM) · $sensors sensors · $Seconds s per phase"
    ''
    '| ' + ($cols -join ' | ') + ' |'
    '|' + (($cols | ForEach-Object { ' --- ' }) -join '|') + '|'
    foreach ($r in $rows) { '| ' + (($cols | ForEach-Object { $r.$_ }) -join ' | ') + ' |' }
} else {
    $rows | Format-Table -AutoSize
}
