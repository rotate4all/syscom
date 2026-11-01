<#
.SYNOPSIS
  Builds the complete SYSCOM package: app, web client, bundled .NET runtime, LibreHardwareMonitor and the PawnIO
  installer, plus the per-component release assets and feed.json used by the in-app updater.

.DESCRIPTION
  Versions come from versions.json at the repository root. To move a component to a newer version, edit that file
  and run this script again. Everything downloaded is verified against the checksum its publisher provides.

  Output (dist\):
    SYSCOM-Setup-<ver>.exe       the installer (Inno Setup; built unless -NoInstaller)
    SYSCOM-<ver>-win-x64.zip     full package: extract and run install.ps1 as administrator (scripted installs)
    syscom-app-<ver>.zip         update asset: server, updater, tray app
    syscom-web-<ver>.zip         update asset: web dashboard
    feed.json                    update feed read by installed copies (publish with the GitHub release)

.EXAMPLE
  pwsh build\package.ps1
.EXAMPLE
  pwsh build\package.ps1 -SkipDownloads   # reuse runtime/PawnIO downloaded by a previous run
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Repository = 'rotate4all/syscom',
    [switch]$SkipDownloads,
    [switch]$NoInstaller   # skip SYSCOM-Setup.exe (it needs Inno Setup, which is fetched when missing)
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$versions = Get-Content (Join-Path $root 'versions.json') -Raw | ConvertFrom-Json
$ver = $versions.syscom
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "SYSCOM-$ver"
$cache = Join-Path $dist 'cache'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Get-Verified([string]$Url, [string]$OutFile, [string]$Sha256, [string]$Sha512) {
    if ($SkipDownloads -and (Test-Path $OutFile)) { Write-Host "    cached $(Split-Path $OutFile -Leaf)"; return }
    Write-Host "    downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    if ($Sha512) { $actual = (Get-FileHash $OutFile -Algorithm SHA512).Hash; $expected = $Sha512 }
    elseif ($Sha256) { $actual = (Get-FileHash $OutFile -Algorithm SHA256).Hash; $expected = $Sha256 }
    else { throw "No checksum published for $Url" }
    if ($actual -ne $expected.ToUpperInvariant()) { Remove-Item $OutFile; throw "Checksum mismatch for $Url" }
}

# ---------------------------------------------------------------- prerequisites
Step 'Checking prerequisites'
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks -match '^10\.')) { throw '.NET 10 SDK not found. Install it with: winget install Microsoft.DotNet.SDK.10' }
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, $cache | Out-Null

# ---------------------------------------------------------------- SYSCOM executables
Step "Building SYSCOM $ver (LibreHardwareMonitor $($versions.librehardwaremonitor))"
$props = @(
    "-p:SyscomVersion=$ver",
    "-p:LhmVersion=$($versions.librehardwaremonitor)",
    "-p:WindowsServicesVersion=$($versions.windowsServices)",
    '-p:DebugType=none'
)
foreach ($proj in 'Syscom.Server', 'Syscom.Updater', 'Syscom.Tray') {
    & dotnet publish (Join-Path $root "src\$proj") -c $Configuration -r win-x64 --self-contained false -o $stage @props
    if ($LASTEXITCODE) { throw "Build of $proj failed" }
}

# ---------------------------------------------------------------- web client
Step "Web dashboard $($versions.web)"
$web = Join-Path $stage 'web'
Copy-Item (Join-Path $root 'web') $web -Recurse
@{ version = $versions.web } | ConvertTo-Json | Set-Content (Join-Path $web 'version.json') -Encoding utf8

# ---------------------------------------------------------------- .NET runtime (private copy in runtime\)
Step ".NET $($versions.dotnet.channel) runtime"
$meta = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$($versions.dotnet.channel)/releases.json"
$rtVersion = if ($versions.dotnet.runtime -eq 'latest') { $meta.'latest-runtime' } else { $versions.dotnet.runtime }
$release = $meta.releases | Where-Object { $_.runtime.version -eq $rtVersion } | Select-Object -First 1
if (-not $release) { throw ".NET runtime $rtVersion not found in release metadata" }
Write-Host "    runtime $rtVersion"
$runtimeDir = Join-Path $stage 'runtime'
foreach ($section in 'aspnetcore-runtime', 'windowsdesktop') {
    $file = $release.$section.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
    $zip = Join-Path $cache "$section-$rtVersion-win-x64.zip"
    Get-Verified -Url $file.url -OutFile $zip -Sha512 $file.hash
    Expand-Archive $zip -DestinationPath $runtimeDir -Force
}

# ---------------------------------------------------------------- PawnIO installer
Step 'PawnIO driver installer'
$drivers = Join-Path $stage 'drivers'
New-Item -ItemType Directory -Force -Path $drivers | Out-Null
$api = if ($versions.pawnio -eq 'latest') { 'latest' } else { "tags/$($versions.pawnio)" }
$ghHeaders = @{ 'User-Agent' = 'SYSCOM-build' }
if ($env:GH_TOKEN) { $ghHeaders.Authorization = "Bearer $env:GH_TOKEN" }
$pawn = Invoke-RestMethod "https://api.github.com/repos/namazso/PawnIO.Setup/releases/$api" -Headers $ghHeaders
$asset = $pawn.assets | Where-Object { $_.name -like '*setup*.exe' } | Select-Object -First 1
if (-not $asset) { throw 'PawnIO release has no setup executable' }
$pawnSha = if ($asset.digest -like 'sha256:*') { $asset.digest.Substring(7) } else { $null }
$pawnVersion = $pawn.tag_name.TrimStart('v')
Write-Host "    PawnIO $pawnVersion"
Get-Verified -Url $asset.browser_download_url -OutFile (Join-Path $cache "PawnIO_setup-$pawnVersion.exe") -Sha256 $pawnSha
Copy-Item (Join-Path $cache "PawnIO_setup-$pawnVersion.exe") (Join-Path $drivers 'PawnIO_setup.exe')
Set-Content (Join-Path $drivers 'PawnIO.version') $pawnVersion

# ---------------------------------------------------------------- scripts, docs, manifest
Step 'Install scripts and notices'
Copy-Item (Join-Path $root 'install\*.ps1') $stage
Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage
Copy-Item (Join-Path $root 'install\README.txt') $stage
[ordered]@{
    syscom = $ver
    web = $versions.web
    dotnet = $rtVersion
    librehardwaremonitor = $versions.librehardwaremonitor
    pawnio = $pawnVersion
    built = (Get-Date).ToString('s')
} | ConvertTo-Json | Set-Content (Join-Path $stage 'components.json') -Encoding utf8

# ---------------------------------------------------------------- installer (Inno Setup)
if (-not $NoInstaller) {
    Step "Installer (Inno Setup $($versions.innosetup))"
    $iscc = & (Join-Path $PSScriptRoot 'get-innosetup.ps1') -Version $versions.innosetup -CacheDir $cache
    & $iscc /Q "/DAppVersion=$ver" "/DPackageDir=$stage" "/O$dist" (Join-Path $root 'install\syscom.iss')
    if ($LASTEXITCODE) { throw 'Building the installer failed' }
}

# ---------------------------------------------------------------- archives + update feed
Step 'Creating archives'
$full = Join-Path $dist "SYSCOM-$ver-win-x64.zip"
Remove-Item $full -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $full

# Update asset for the "SYSCOM" component: our own executables only (runtime, LHM and web update separately).
$appOnly = Join-Path $dist 'app-only'
Remove-Item $appOnly -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $appOnly | Out-Null
Get-ChildItem $stage -File | Where-Object {
    $_.Name -like 'syscom*' -or $_.Name -in 'Microsoft.Extensions.Hosting.WindowsServices.dll', 'System.ServiceProcess.ServiceController.dll'
} | Copy-Item -Destination $appOnly
$appZip = Join-Path $dist "syscom-app-$ver.zip"
$webZip = Join-Path $dist "syscom-web-$($versions.web).zip"
Remove-Item $appZip, $webZip -ErrorAction SilentlyContinue
Compress-Archive -Path "$appOnly\*" -DestinationPath $appZip
Compress-Archive -Path "$web\*" -DestinationPath $webZip

$base = "https://github.com/$Repository/releases/download/v$ver"
[ordered]@{
    syscom = [ordered]@{ version = $ver; url = "$base/$(Split-Path $appZip -Leaf)"; sha256 = (Get-FileHash $appZip -Algorithm SHA256).Hash.ToLowerInvariant() }
    web = [ordered]@{ version = $versions.web; url = "$base/$(Split-Path $webZip -Leaf)"; sha256 = (Get-FileHash $webZip -Algorithm SHA256).Hash.ToLowerInvariant() }
} | ConvertTo-Json | Set-Content (Join-Path $dist 'feed.json') -Encoding utf8

Step 'Done'
Get-ChildItem $dist -File | Format-Table Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
Write-Host "Install: run SYSCOM-Setup-$ver.exe (or extract $(Split-Path $full -Leaf) and run install.ps1 as administrator)."
