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
Real public releases must sign — see scripts/sign.ps1 (Phase 5.B).

.EXAMPLE
pwsh scripts/publish.ps1                 # uses AppInfo.Version
pwsh scripts/publish.ps1 -Version 0.2.0  # explicit
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [switch]$NoSign
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
    '--packAuthors', 'snw.dev',
    '--outputDir', $OutputDir
)
if ($NoSign) {
    Write-Host '   (unsigned build — do not distribute)' -ForegroundColor Yellow
}
& $vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host ""
Write-Host "==> Done. Installer at:" -ForegroundColor Green
Get-ChildItem $OutputDir -Filter "*Setup.exe" | ForEach-Object { Write-Host "    $($_.FullName)" }
