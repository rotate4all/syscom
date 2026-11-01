<#
.SYNOPSIS
  Returns the path of ISCC.exe (the Inno Setup compiler) of the given version, installing that version when needed.

.DESCRIPTION
  Uses an installed Inno Setup of exactly that version (Program Files, or a previous run's copy in -CacheDir);
  otherwise downloads the official installer from the Inno Setup GitHub releases, checks it against the SHA-256
  that GitHub publishes for it, and installs it quietly into -CacheDir (just for the current user, nothing else on
  the PC changes). The version is pinned in versions.json ("innosetup"); the Dependencies workflow flags newer ones.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$CacheDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$major = $Version.Split('.')[0]

function Test-Iscc([string]$path) {
    if (-not $path -or -not (Test-Path $path)) { return $false }
    try {
        $have = [version]((Get-Item $path).VersionInfo.ProductVersion -replace '[^\d.].*$', '')
        $want = [version]$Version
        return $have.Major -eq $want.Major -and $have.Minor -eq $want.Minor -and $have.Build -eq $want.Build
    } catch { return $false }
}

$candidates = @(
    (Join-Path $CacheDir "innosetup-$Version\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup $major\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup $major\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup $major\ISCC.exe")
)
foreach ($c in $candidates) { if (Test-Iscc $c) { Write-Host "    Inno Setup $Version at $c"; return $c } }

$tag = 'is-' + $Version.Replace('.', '_')
$headers = @{ 'User-Agent' = 'SYSCOM-build' }
if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$release = Invoke-RestMethod "https://api.github.com/repos/jrsoftware/issrc/releases/tags/$tag" -Headers $headers
$asset = $release.assets | Where-Object { $_.name -eq "innosetup-$Version-x64.exe" } | Select-Object -First 1
if (-not $asset) { throw "Inno Setup $Version has no x64 installer in its GitHub release" }
if ($asset.digest -notlike 'sha256:*') { throw "GitHub publishes no checksum for $($asset.name)" }

New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
$setup = Join-Path $CacheDir $asset.name
Write-Host "    downloading $($asset.browser_download_url)"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $setup -UseBasicParsing
if ((Get-FileHash $setup -Algorithm SHA256).Hash -ne $asset.digest.Substring(7).ToUpperInvariant()) {
    Remove-Item $setup
    throw "Checksum mismatch for $($asset.name)"
}

$dir = Join-Path $CacheDir "innosetup-$Version"
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/NOICONS', "/DIR=`"$dir`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installing Inno Setup $Version failed ($($p.ExitCode))" }
$iscc = Join-Path $dir 'ISCC.exe'
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found in $dir" }
Write-Host "    Inno Setup $Version installed to $dir"
return $iscc
