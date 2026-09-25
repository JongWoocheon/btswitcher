param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version,
    [Parameter(Mandatory)]
    [string] $Publisher,
    [Parameter(Mandatory)]
    [string] $Directory
)

# Run this on a clean Windows machine after any signing provider returns the MSIX files.
. "$PSScriptRoot/common.ps1"
$directoryPath = (Resolve-Path -LiteralPath $Directory -ErrorAction Stop).Path
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$signtool = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools/10.0.26100.4948/bin/10.0.26100.0/x64/signtool.exe'
if (-not (Test-Path -LiteralPath $signtool)) {
    $dotnet = Get-ProjectDotnet
    & $dotnet restore (Join-Path $RepoRoot 'src/BtSwitcher.Extension/BtSwitcher.Extension.csproj') -v:minimal
    Assert-NativeSuccess 'Extension dependency restore'
    if (-not (Test-Path -LiteralPath $signtool)) {
        throw 'SignTool is missing after extension dependency restore.'
    }
}

$checksums = foreach ($architecture in 'x64', 'arm64') {
    $packageName = "BtSwitcher_${Version}_${architecture}.msix"
    $package = Join-Path $directoryPath $packageName
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Missing release package: $package" }

    & $signtool verify /pa /v $package | Out-Null
    Assert-NativeSuccess "$architecture signature verification"
    $signature = Get-AuthenticodeSignature -FilePath $package
    if (-not $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne $Publisher) {
        throw "$packageName was not signed by the expected publisher."
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (-not $entry) { throw "$packageName has no AppxManifest.xml." }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $identity = $manifest.Package.Identity
        if ($identity.Name -ne 'BtSwitcherExtension' -or
            $identity.Publisher -cne $Publisher -or
            $identity.Version -ne $Version -or
            $identity.ProcessorArchitecture -ne $architecture) {
            throw "$packageName has an unexpected package identity."
        }
    }
    finally { $archive.Dispose() }

    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName"
    Write-Host "Verified signature and identity: $packageName"
}

$checksums | Set-Content -LiteralPath (Join-Path $directoryPath 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Verified both release packages in $directoryPath"
