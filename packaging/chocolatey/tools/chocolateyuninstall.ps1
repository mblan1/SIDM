$ErrorActionPreference = 'Stop'

# Velopack writes an uninstaller into the per-user app folder. The DisplayName
# in HKCU's Uninstall registry lets choco discover it without us hard-coding
# the path.
$key = Get-UninstallRegistryKey -SoftwareName 'SIDM*'
if (-not $key) {
    Write-Warning 'SIDM uninstall registry entry not found; nothing to do.'
    return
}

$packageArgs = @{
    packageName    = 'sidm'
    fileType       = 'EXE'
    silentArgs     = '--silent --uninstall'
    validExitCodes = @(0)
    file           = $key.UninstallString
}
Uninstall-ChocolateyPackage @packageArgs
