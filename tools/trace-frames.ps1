#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Records the frame events SYSCOM's frame counter uses, for a few seconds, so frame rate problems (frame generation,
  a game that isn't detected) can be looked into. Uses only Windows' own logman and tracerpt.

.DESCRIPTION
  Start the game first, get into gameplay with the setting you want to check (for example frame generation on), then
  run this in an administrator PowerShell. Keep the game in front while it records.

  It records:
    Microsoft-Windows-DXGI          present calls (what SYSCOM counts as "Frame rate")
    Microsoft-Windows-D3D9          present calls of older games
    NVIDIA PC Latency Stats         Reflex frame markers (what SYSCOM counts as "Native frame rate" on NVIDIA)
    Intel-PresentMon                frame types reported by XeSS frame generation and AMD Fluid Motion Frames
    Microsoft-Windows-DxgKrnl       the graphics kernel's present and flip events (frames that reach the screen)

  Output, in a "frame-trace" folder next to this repository's root (or -Out):
    frames.etl      the raw recording
    frames.xml      every event decoded by tracerpt
    summary.txt     event counts per provider and event

  Summarise it with tools\analyze-frame-trace.py (Python 3): rendered, presented and displayed frames per second.

.EXAMPLE
  .\tools\trace-frames.ps1              # 10 seconds
  .\tools\trace-frames.ps1 -Seconds 20
#>
param(
    [ValidateRange(3, 60)][int]$Seconds = 10,
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'frame-trace')
)
$ErrorActionPreference = 'Stop'
$name = 'SYSCOM-frame-trace'

New-Item -ItemType Directory -Force $Out | Out-Null
$etl = Join-Path $Out 'frames.etl'
$xml = Join-Path $Out 'frames.xml'
$summary = Join-Path $Out 'summary.txt'
$providers = Join-Path $Out 'providers.txt'
Remove-Item $etl, $xml, $summary -ErrorAction SilentlyContinue

# Provider, keywords, level (4 = information, 5 = verbose).
@(
    '{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9} 0xffffffffffffffff 0x4'   # Microsoft-Windows-DXGI
    '{783ACA0A-790E-4D7F-8451-AA850511C6B9} 0xffffffffffffffff 0x4'   # Microsoft-Windows-D3D9
    '{0D216F06-82A6-4D49-BC4F-8F38AE56EFAB} 0xffffffffffffffff 0x5'   # NVIDIA PC Latency Stats (Reflex)
    '{ECAA4712-4644-442F-B94C-A32F6CF8A499} 0xffffffffffffffff 0x5'   # Intel-PresentMon (frame types)
    '{802EC45A-1E99-4B83-9920-87C98277BA9D} 0x1 0x4'                  # Microsoft-Windows-DxgKrnl, Base (present, flip)
) | Set-Content -Encoding ascii $providers

logman stop $name -ets 2>$null | Out-Null
Write-Host "Recording frame events for $Seconds seconds. Keep the game in front..."
logman start $name -ets -pf $providers -o $etl -bs 1024 -nb 64 512 -mode Sequential | Out-Null
if ($LASTEXITCODE -ne 0) { throw "logman could not start the recording (exit code $LASTEXITCODE)." }
try {
    Start-Sleep -Seconds $Seconds
}
finally {
    logman stop $name -ets | Out-Null
}

Write-Host 'Decoding...'
tracerpt $etl -o $xml -of XML -summary $summary -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "tracerpt could not decode the recording (exit code $LASTEXITCODE)." }

$size = [math]::Round((Get-Item $xml).Length / 1MB, 1)
Write-Host "Done: $Out (frames.xml is $size MB)."
