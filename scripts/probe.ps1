# Read-only: never sends connect/disconnect/default-output requests.
. "$PSScriptRoot/common.ps1"
$dotnet = Get-ProjectDotnet
Push-Location $RepoRoot
try {
    & $dotnet run --project src/BtSwitcher.Cli -- probe
    Assert-NativeSuccess 'Read-only probe'
}
finally { Pop-Location }
