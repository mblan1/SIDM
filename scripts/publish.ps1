#!/usr/bin/env pwsh
<#
.SYNOPSIS
Packages SIDM into a Velopack installer ("Setup.exe") that any Windows user
can double-click to install.

.DESCRIPTION
Phase 5.A flow:
  1. dotnet publish SIDM.App   (self-contained, win-x64, single-file).
  2. dotnet publish SIDM.BrowserHost (likewise; the NMH bridge needs to ship
     next to the app so SIDM.App's NativeHostRegistration can resolve it).
  3. vpk pack — bundles the publish folder into a versioned NuGet, generates
     Setup.exe, RELEASES, and the delta package for in-place updates.

.PARAMETER Version
SemVer to stamp on the build. Defaults to the AppInfo.Version constant
("0.1.0" at the time of writing). Use --pre suffixes for prereleases:
  pwsh scripts/publish.ps1 -Version 0.2.0-beta.1

.PARAMETER OutputDir
Folder receiving Setup.exe + RELEASES. Defaults to <repo>/releases.

.PARAMETER NoSign
Skip Authenticode signing. Useful for local "does this even pack" testing.
Real public releases must sign.

.PARAMETER CertificateThumbprint
SHA-1 thumbprint of the Authenticode signing certificate installed in the
current user's certificate store (Cert:\CurrentUser\My). Vpk reads the cert
from there and signs every shipped exe + dll in the package, then signs the
generated Setup.exe.

.PARAMETER TimestampUrl
RFC 3161 timestamp server. Defaults to DigiCert's free one. Without a
timestamp the signature expires when the cert does.

.EXAMPLE
pwsh scripts/publish.ps1                 # uses AppInfo.Version, unsigned
pwsh scripts/publish.ps1 -Version 0.2.0  # explicit version, unsigned
pwsh scripts/publish.ps1 -CertificateThumbprint ABCD1234... # signed release
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [switch]$NoSign,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $RepoRoot

if (-not $Version) {
    # Pull the version from AppInfo.cs so the source of truth stays one file.
    $appInfo = Get-Content (Join-Path $RepoRoot 'src/SIDM.Core/AppInfo.cs') -Raw
    if ($appInfo -match 'Version\s*=\s*"([^"]+)"') {
        $Version = $Matches[1]
    } else {
        throw "Could not read Version from AppInfo.cs."
    }
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'releases'
}

Write-Host "==> Publishing SIDM $Version" -ForegroundColor Cyan

$publishDir = Join-Path $RepoRoot 'publish/sidm'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Path $publishDir | Out-Null

# 1) Publish SIDM.App (the WPF UI).
Write-Host "==> dotnet publish SIDM.App" -ForegroundColor Cyan
dotnet publish src/SIDM.App/SIDM.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish SIDM.App failed" }

# 2) Publish SIDM.BrowserHost into the same folder. The NMH manifests
#    that --register-hosts writes point at this exe path, so it must live
#    next to SIDM.App.exe in the installed layout.
Write-Host "==> dotnet publish SIDM.BrowserHost" -ForegroundColor Cyan
dotnet publish src/SIDM.BrowserHost/SIDM.BrowserHost.csproj `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish SIDM.BrowserHost failed" }

# 2.5) Build + zip the browser extensions next to the app so
#      BrowserExtensionInstaller can extract them on first run without an
#      internet round-trip. Each extension's npm build emits to dist/ and we
#      zip that directly. Requires Node 18+ on PATH.
$extOutDir = Join-Path $publishDir 'extensions'
New-Item -ItemType Directory -Path $extOutDir -Force | Out-Null

$extensions = @(
    @{ Name = 'Chromium'; Folder = 'src/SIDM.Extension.Chrome' },
    @{ Name = 'Firefox';  Folder = 'src/SIDM.Extension.Firefox' }
)
foreach ($ext in $extensions) {
    $extPath = Join-Path $RepoRoot $ext.Folder
    Write-Host "==> Building $($ext.Name) extension in $($ext.Folder)" -ForegroundColor Cyan
    Push-Location $extPath
    try {
        npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm install failed in $($ext.Folder)" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed in $($ext.Folder)" }

        $distPath = Join-Path $extPath 'dist'
        if (-not (Test-Path $distPath)) { throw "Expected $distPath after build" }

        $zipName = "SIDM-Extension-$($ext.Name)-$Version.zip"
        $zipPath = Join-Path $extOutDir $zipName
        if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
        Compress-Archive -Path (Join-Path $distPath '*') -DestinationPath $zipPath
        Write-Host "    wrote $zipPath" -ForegroundColor DarkGray
    }
    finally {
        Pop-Location
    }
}

# 3) Velopack pack. Requires the vpk tool — install it once globally with:
#      dotnet tool install -g vpk
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    throw @"
'vpk' not found on PATH. Install it once with:
  dotnet tool install -g vpk
"@
}

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

Write-Host "==> vpk pack" -ForegroundColor Cyan
$packArgs = @(
    'pack',
    '--packId', 'SIDM',
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', 'SIDM.App.exe',
    '--packTitle', 'Snw Internet Download Manager',
    '--packAuthors', 'snw',
    '--outputDir', $OutputDir
)

# Authenticode signing. vpk drives signtool internally — we just pass the
# parameters. Without a thumbprint we leave the package unsigned (and warn).
$shouldSign = -not $NoSign -and -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
if ($shouldSign) {
    Write-Host "   signing with cert thumbprint $CertificateThumbprint" -ForegroundColor Cyan
    # /sha1 selects the cert by thumbprint from the current user's store.
    # /fd SHA256 forces a SHA-256 file digest (Win8+ requirement).
    # /tr + /td add an RFC 3161 timestamp so the signature outlives the cert.
    $signParams = "/sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256"
    $packArgs += @('--signParams', $signParams)
} else {
    Write-Host '   (unsigned build — do not distribute)' -ForegroundColor Yellow
}

& $vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

# 4) MSI variant. Same payload, but vpk emits a Windows Installer wizard with
#    a PerUser/PerMachine chooser — for users who want a system-wide install
#    or for IT-managed deployments. Adds SIDMSetup.msi alongside the EXE.
Write-Host "==> vpk pack --msi --instLocation Either" -ForegroundColor Cyan
$msiArgs = $packArgs + @('--msi', '--instLocation', 'Either')
& $vpk @msiArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "   (skipping MSI variant — current vpk version does not support --msi)" -ForegroundColor Yellow
    $global:LASTEXITCODE = 0
}

# 5) Build the SetupLauncher and slot it in front of the Velopack EXE so the
#    user-facing SIDMSetup.exe is the one with the folder-picker UI. The
#    Velopack one-click EXE is preserved as SIDMSetup-Bootstrap.exe for
#    silent installs and as the target the launcher actually spawns.
$bootstrapExe = Join-Path $OutputDir 'SIDMSetup-Bootstrap.exe'
$velopackExeCandidates = @(
    (Join-Path $OutputDir 'SIDMSetup.exe'),
    (Join-Path $OutputDir 'SIDM-win-Setup.exe')
)
$velopackExe = $velopackExeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($velopackExe) {
    if (Test-Path $bootstrapExe) { Remove-Item -Force $bootstrapExe }
    Move-Item $velopackExe $bootstrapExe
} elseif (-not (Test-Path $bootstrapExe)) {
    throw "Expected Velopack setup exe in $OutputDir (looked for: $($velopackExeCandidates -join ', '))."
}

Write-Host "==> Publishing SIDM.SetupLauncher" -ForegroundColor Cyan
$launcherStaging = Join-Path $RepoRoot 'publish/launcher'
if (Test-Path $launcherStaging) { Remove-Item -Recurse -Force $launcherStaging }
New-Item -ItemType Directory -Path $launcherStaging | Out-Null
dotnet publish src/SIDM.SetupLauncher/SIDM.SetupLauncher.csproj `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o $launcherStaging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish SIDM.SetupLauncher failed" }

Copy-Item (Join-Path $launcherStaging 'SIDMSetup.exe') $OutputDir -Force

Write-Host ""
Write-Host "==> Done. Install artifacts in ${OutputDir}:" -ForegroundColor Green
Write-Host "    SIDMSetup.exe            — folder-picker launcher (front door)" -ForegroundColor Green
Write-Host "    SIDMSetup-Bootstrap.exe  — Velopack one-click / silent" -ForegroundColor Green
Get-ChildItem $OutputDir -Filter "*.msi" | ForEach-Object {
    Write-Host "    $($_.Name.PadRight(24)) — PerUser/PerMachine wizard" -ForegroundColor Green
}
