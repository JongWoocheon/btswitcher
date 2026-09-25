# Register a development layout for the current user. Does not change Developer Mode.
. "$PSScriptRoot/common.ps1"
$manifest = Join-Path $RepoRoot 'artifacts/app/AppxManifest.xml'
if (-not (Test-Path $manifest)) { throw 'Run scripts/build.ps1 first.' }
Add-AppxPackage -Register $manifest -ErrorAction Stop
Get-AppxPackage -Name BtSwitcherExtension | Select-Object Name,Version,InstallLocation
Write-Host 'Installed. In Command Palette, run Reload, then search bt.'
