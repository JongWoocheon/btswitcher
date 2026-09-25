param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
. "$PSScriptRoot/common.ps1"
$dotnet = Get-ProjectDotnet
Push-Location $RepoRoot
try {
    & $dotnet build src/BtSwitcher.Cli -c $Configuration -v:minimal
    Assert-NativeSuccess 'CLI build'
    & $dotnet test tests/BtSwitcher.Tests -c $Configuration -v:minimal --logger 'trx;LogFileName=core.trx' --results-directory artifacts/tests
    Assert-NativeSuccess 'Core tests'
    & $dotnet publish src/BtSwitcher.Extension -c $Configuration -r win-x64 -p:Platform=x64 --self-contained true -o artifacts/app -v:minimal
    Assert-NativeSuccess 'Extension publish'

    $app = Join-Path $RepoRoot 'artifacts/app'
    $output = Join-Path $RepoRoot "src/BtSwitcher.Extension/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64"
    Copy-Item -LiteralPath (Join-Path $output 'AppxManifest.xml') -Destination $app -Force
    New-Item -ItemType Directory -Force (Join-Path $app 'Assets'),(Join-Path $app 'Public'),(Join-Path $app 'licenses') | Out-Null
    Copy-Item -Path 'src/BtSwitcher.Extension/Assets/*.png' -Destination (Join-Path $app 'Assets') -Force
    Copy-Item -LiteralPath LICENSE,NOTICE -Destination $app -Force
    Copy-Item -LiteralPath licenses/PowerToys-MIT.txt -Destination (Join-Path $app 'licenses') -Force
    Set-Content -LiteralPath (Join-Path $app 'Public/README.txt') -Value 'Bluetooth Switcher Command Palette extension.' -Encoding utf8

    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $makeappx = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools/10.0.26100.4948/bin/10.0.26100.0/x64/makeappx.exe'
    & $makeappx pack /d $app /p (Join-Path $RepoRoot 'artifacts/BtSwitcher-x64.msix') /o *> artifacts/package.log
    if ($LASTEXITCODE -ne 0) { Get-Content artifacts/package.log -Tail 25; throw 'MSIX validation failed.' }
    Write-Host 'Built artifacts/BtSwitcher-x64.msix (unsigned) and artifacts/app (development layout).'
}
finally { Pop-Location }
