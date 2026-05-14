$ErrorActionPreference = 'Stop'

# Per-release: update Url64 + Checksum64 (sha256 of Setup.exe).
$packageArgs = @{
    packageName    = 'sidm'
    fileType       = 'EXE'
    url64bit       = 'https://github.com/mblan1/SIDM/releases/download/v0.1.0/SIDM-Setup.exe'
    checksum64     = 'REPLACE_WITH_SHA256_OF_SETUP_EXE'
    checksumType64 = 'sha256'

    # Velopack-generated installers run silently with --silent.
    silentArgs     = '--silent'
    validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
