<#
.SYNOPSIS
  Installs or upgrades SYSCOM on this PC. Run from the extracted package folder as administrator.

.DESCRIPTION
  - copies SYSCOM (with its private .NET runtime) to Program Files
  - installs or updates the PawnIO driver when the bundled one is newer than what's installed
  - registers the "SYSCOM" Windows service (starts at boot, restarts on failure)
  - opens the dashboard ports (TCP, HTTP and HTTPS) and discovery port (UDP) for private networks
  - starts the tray app at every logon, with administrator rights, so it can manage updates
  - adds Start menu entries: "SYSCOM" (the management window) and "SYSCOM dashboard"
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'SYSCOM'),
    [int]$Port = 47810,
    [int]$DiscoveryPort = 47811,
    [int]$HttpsPort = 47812,
    [switch]$SkipPawnIO,
    [switch]$NoTray,
    [switch]$Pause   # keep the window open at the end (set when the script restarts itself as administrator)
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$service = 'SYSCOM'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Restarting as administrator...'
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"$InstallDir`" -Port $Port -DiscoveryPort $DiscoveryPort -HttpsPort $HttpsPort -Pause$(if ($SkipPawnIO) {' -SkipPawnIO'})$(if ($NoTray) {' -NoTray'})"
    exit
}

# A copy of everything shown goes to %ProgramData%\SYSCOM\install.log, and a failure keeps the window open.
$logDir = Join-Path $env:ProgramData 'SYSCOM'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
try { Start-Transcript -Path (Join-Path $logDir 'install.log') -Force | Out-Null } catch { }
trap {
    Write-Host "`nInstalling SYSCOM failed: $_" -ForegroundColor Red
    Write-Host "Nothing was started. Fix the cause and run install.ps1 again; details: $(Join-Path $logDir 'install.log')"
    try { Stop-Transcript | Out-Null } catch { }
    if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
if (-not (Test-Path (Join-Path $source 'syscom.exe'))) { throw "Run this script from the extracted SYSCOM package folder." }

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- stop a running copy
Step 'Stopping running SYSCOM (if any)'
Get-Process syscom-tray -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service $service -ErrorAction SilentlyContinue) {
    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='$service'").ProcessId
    Stop-Service $service -Force -ErrorAction SilentlyContinue
    (Get-Service $service).WaitForStatus('Stopped', '00:01:00')
    # "Stopped" comes before the process is gone (a crashing one lingers while Windows writes its dump), and until
    # then its files can't be replaced.
    if ($servicePid) {
        Wait-Process -Id $servicePid -Timeout 60 -ErrorAction SilentlyContinue
        Get-Process -Id $servicePid -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}
# Anything else still running from the install folder (an updater mid-run, a server started by hand).
$installPrefix = $InstallDir.TrimEnd('\').ToLowerInvariant() + '\'
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.ToLowerInvariant().StartsWith($installPrefix) } |
    ForEach-Object { Write-Host "    stopping $($_.Name) ($($_.Id))"; $_ | Stop-Process -Force; $_.WaitForExit(10000) | Out-Null }

# ---------------------------------------------------------------- files
Step "Copying files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$exclude = 'install.ps1', 'uninstall.ps1', 'README.txt'
foreach ($item in @(Get-ChildItem $source | Where-Object { $_.Name -notin $exclude })) {
    # A file can stay locked for a moment after its program exits (antivirus scans, crash dumps): retry a few times.
    for ($try = 1; ; $try++) {
        try { Copy-Item $item.FullName -Destination $InstallDir -Recurse -Force; break }
        catch {
            if ($try -ge 6) { throw }
            Write-Host "    $($_.Exception.Message) Retrying..."
            Start-Sleep -Seconds 3
        }
    }
}
Copy-Item (Join-Path $source 'uninstall.ps1') $InstallDir -Force
# Old program files an in-app update had to rename aside because they were in use.
Get-ChildItem $InstallDir -Filter '*.syscom-old' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- PawnIO
if (-not $SkipPawnIO) {
    Step 'PawnIO driver'
    $bundled = (Get-Content (Join-Path $source 'drivers\PawnIO.version') -ErrorAction SilentlyContinue)
    $installed = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' -ErrorAction SilentlyContinue).DisplayVersion
    $needs = -not $installed
    if ($installed -and $bundled) { try { $needs = [version]$bundled -gt [version]$installed } catch { $needs = $false } }
    if ($needs) {
        Write-Host "    installing PawnIO $bundled (installed: $(if ($installed) { $installed } else { 'none' }))"
        $p = Start-Process (Join-Path $source 'drivers\PawnIO_setup.exe') -ArgumentList '-install', '-silent' -Wait -PassThru
        if ($p.ExitCode -ne 0) { Write-Warning "PawnIO setup exited with $($p.ExitCode). CPU temperatures may be missing; you can update it later from the tray app." }
    } else {
        Write-Host "    PawnIO $installed is up to date"
    }
}

# ---------------------------------------------------------------- service
Step 'Windows service'
$exe = Join-Path $InstallDir 'syscom.exe'
if (-not (Get-Service $service -ErrorAction SilentlyContinue)) {
    New-Service -Name $service -BinaryPathName "`"$exe`"" -DisplayName 'SYSCOM System Companion' `
        -Description 'Streams this PC''s hardware sensors to the SYSCOM web dashboard on your network.' -StartupType Automatic | Out-Null
} else {
    & sc.exe config $service binPath= "`"$exe`"" start= auto | Out-Null
}
# Restart automatically if it ever crashes, or stops with an error.
& sc.exe failure $service reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
& sc.exe failureflag $service 1 | Out-Null

# Ports live in %ProgramData%\SYSCOM\config.json (created on first start with defaults).
$configFile = Join-Path $env:ProgramData 'SYSCOM\config.json'
if ($Port -ne 47810 -or $DiscoveryPort -ne 47811 -or $HttpsPort -ne 47812 -or (Test-Path $configFile)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $configFile) | Out-Null
    $config = if (Test-Path $configFile) { Get-Content $configFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    $config | Add-Member -NotePropertyName Port -NotePropertyValue $Port -Force
    $config | Add-Member -NotePropertyName DiscoveryPort -NotePropertyValue $DiscoveryPort -Force
    $config | Add-Member -NotePropertyName HttpsPort -NotePropertyValue $HttpsPort -Force
    $config | ConvertTo-Json | Set-Content $configFile -Encoding utf8
}

# ---------------------------------------------------------------- firewall
Step 'Firewall (private networks only)'
Get-NetFirewallRule -DisplayName 'SYSCOM*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'SYSCOM dashboard' -Direction Inbound -Protocol TCP -LocalPort $Port -Profile Private -Action Allow -Program $exe | Out-Null
New-NetFirewallRule -DisplayName 'SYSCOM dashboard (HTTPS)' -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -Profile Private -Action Allow -Program $exe | Out-Null
New-NetFirewallRule -DisplayName 'SYSCOM discovery' -Direction Inbound -Protocol UDP -LocalPort $DiscoveryPort -Profile Private -Action Allow -Program $exe | Out-Null

# ---------------------------------------------------------------- tray app at logon
if (-not $NoTray) {
    Step 'Tray app'
    $tray = Join-Path $InstallDir 'syscom-tray.exe'
    $action = New-ScheduledTaskAction -Execute $tray
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0
    $taskPrincipal = New-ScheduledTaskPrincipal -GroupId 'BUILTIN\Administrators' -RunLevel Highest
    Register-ScheduledTask -TaskName 'SYSCOM Tray' -Action $action -Trigger $trigger -Settings $settings -Principal $taskPrincipal -Force | Out-Null
}

# ---------------------------------------------------------------- Start menu
Step 'Start menu'
$menu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SYSCOM'
New-Item -ItemType Directory -Force -Path $menu | Out-Null
$trayExe = Join-Path $InstallDir 'syscom-tray.exe'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $menu 'SYSCOM.lnk'))
$link.TargetPath = $trayExe
$link.Arguments = '--open'
$link.WorkingDirectory = $InstallDir
$link.Description = 'Manage SYSCOM: health, updates and phone setup'
$link.IconLocation = "$trayExe,0"
$link.Save()
Set-Content (Join-Path $menu 'SYSCOM dashboard.url') "[InternetShortcut]`r`nURL=http://localhost:$Port/`r`nIconFile=$trayExe`r`nIconIndex=0" -Encoding ascii
Write-Host "    $menu"

# ---------------------------------------------------------------- start
Step 'Starting SYSCOM'
Start-Service $service
$ok = $false
foreach ($i in 1..30) {
    try { Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 2 | Out-Null; $ok = $true; break } catch { Start-Sleep 1 }
}
if (-not $ok) { Write-Warning 'SYSCOM did not answer on its health endpoint. Check the Windows event log (Application).' }
if (-not $NoTray) { Start-ScheduledTask -TaskName 'SYSCOM Tray' }

$pwFile = Join-Path $env:ProgramData 'SYSCOM\initial-password.txt'
# The real network: adapters with a default route (Wi-Fi, Ethernet). Hyper-V and WSL adapters have none and phones
# can't reach them. Without any default route (offline PC), list every address.
$all = @(Get-NetIPAddress -AddressFamily IPv4 -PrefixOrigin Dhcp, Manual -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254*' })
$routed = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object InterfaceIndex)
$ips = @($all | Where-Object { $routed -contains $_.InterfaceIndex } | ForEach-Object IPAddress)
if (-not $ips) { $ips = @($all | ForEach-Object IPAddress) }
Write-Host ''
Write-Host 'SYSCOM is installed.' -ForegroundColor Green
foreach ($ip in $ips) { Write-Host "  Dashboard:  http://${ip}:$Port" }
if ($ips) { Write-Host "  Phone app:  http://$($ips[0]):$Port/trust  (or tray icon > Set up phone)" }
if (Test-Path $pwFile) { Write-Host "  Password:   $(Get-Content $pwFile)  (change it from the tray app)" }
Write-Host '  Manage it from the SYSCOM icon in the notification area, or Start menu > SYSCOM.'
try { Stop-Transcript | Out-Null } catch { }
if ($Pause) { Read-Host "`nPress Enter to close" | Out-Null }
