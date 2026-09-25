# Downloads the pinned official SDK into this repository. No system installation.
. "$PSScriptRoot/common.ps1"
$version = (Get-Content -Raw (Join-Path $RepoRoot 'global.json') | ConvertFrom-Json).sdk.version
$toolsPath = Join-Path $RepoRoot '.tools'
$dotnetPath = Join-Path $toolsPath 'dotnet/dotnet.exe'
if ((Test-Path $dotnetPath) -and ((& $dotnetPath --list-sdks) -match ([regex]::Escape($version)))) {
    Write-Host "SDK $version is already available."
    exit 0
}
New-Item -ItemType Directory -Force -Path $toolsPath | Out-Null
$metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$sdk = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $version)[0]
if (-not $sdk) { throw "Official release metadata does not contain SDK $version." }
$package = @($sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' })[0]
if (-not $package) { throw 'The win-x64 SDK archive is unavailable.' }
$archive = Join-Path $toolsPath 'dotnet-sdk.zip'
if (-not (Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA512).Hash -ne $package.hash) {
    Invoke-WebRequest -Uri $package.url -OutFile $archive
}
if ((Get-FileHash $archive -Algorithm SHA512).Hash -ne $package.hash) { throw 'SDK SHA-512 verification failed.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolsPath 'dotnet') -Force
& $dotnetPath --info
Assert-NativeSuccess 'SDK verification'
