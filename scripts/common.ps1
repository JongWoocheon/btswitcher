$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Get-ProjectDotnet {
    $local = Join-Path $RepoRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $sdks = & $command.Source --list-sdks
        if ($sdks -match '^10\.') { return $command.Source }
    }
    throw 'No .NET 10 SDK found. Run scripts/bootstrap.ps1 first.'
}

function Assert-NativeSuccess([string] $Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit $LASTEXITCODE)." }
}
