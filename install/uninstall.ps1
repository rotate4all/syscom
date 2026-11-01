<#
.SYNOPSIS
  Removes SYSCOM from this PC. Run as administrator.
.PARAMETER KeepData
  Keep %ProgramData%\SYSCOM (password, settings, update history).
.NOTES
  The PawnIO driver is left installed because other monitoring tools may use it.
  Remove it from "Installed apps" if you no longer need it.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'SYSCOM'),
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as administrator.' }

Get-Process syscom-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName 'SYSCOM Tray' -Confirm:$false -ErrorAction SilentlyContinue
if (Get-Service SYSCOM -ErrorAction SilentlyContinue) {
    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='SYSCOM'").ProcessId
    Stop-Service SYSCOM -Force -ErrorAction SilentlyContinue
    # "Stopped" comes before the process is gone; its files can't be deleted until then.
    if ($servicePid) {
        Wait-Process -Id $servicePid -Timeout 60 -ErrorAction SilentlyContinue
        Get-Process -Id $servicePid -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    & sc.exe delete SYSCOM | Out-Null
}
Get-NetFirewallRule -DisplayName 'SYSCOM*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SYSCOM') -Recurse -Force -ErrorAction SilentlyContinue

# This script may be running from inside the install folder; remove everything else, then schedule the rest.
Get-ChildItem $InstallDir -Exclude 'uninstall.ps1' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Start-Process cmd.exe -WindowStyle Hidden -ArgumentList "/c timeout /t 2 >nul & rmdir /s /q `"$InstallDir`""

if (-not $KeepData) { Remove-Item (Join-Path $env:ProgramData 'SYSCOM') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'SYSCOM has been removed. (The PawnIO driver was left installed.)' -ForegroundColor Green
