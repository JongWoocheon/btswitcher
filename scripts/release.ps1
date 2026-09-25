param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version,
    [string] $CertificatePath,
    [switch] $PackageOnly,
    [string] $Publisher,
    [string] $PublisherDisplayName,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $AllowSelfSignedForTesting
)

# Direct-distribution release: package both architectures, then sign and verify
# with a PFX or leave clearly named unsigned inputs for an external signer.
# The PFX password is read from the environment, not passed to SignTool.
. "$PSScriptRoot/common.ps1"

$parsedVersion = [version]$Version
if (@($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, $parsedVersion.Revision) |
    Where-Object { $_ -lt 0 -or $_ -gt 65535 }) {
    throw 'Each MSIX version component must be between 0 and 65535.'
}

if ($PackageOnly) {
    if ($CertificatePath) { throw 'Use either -PackageOnly or -CertificatePath, not both.' }
    if (-not $Publisher) { throw 'An external signer requires -Publisher to match its certificate subject exactly.' }
    if (-not $PublisherDisplayName) { $PublisherDisplayName = $Publisher }
}
else {
    if (-not $CertificatePath) { throw 'A production release requires -CertificatePath.' }
    if ($Publisher) { throw 'Publisher is read from the signing certificate; do not pass -Publisher.' }
    if (-not $env:BTSWITCHER_SIGNING_PFX_PASSWORD) {
        throw 'Set BTSWITCHER_SIGNING_PFX_PASSWORD before running the release script.'
    }
    $certificatePath = (Resolve-Path -LiteralPath $CertificatePath -ErrorAction Stop).Path
    $password = ConvertTo-SecureString $env:BTSWITCHER_SIGNING_PFX_PASSWORD -AsPlainText -Force
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certificatePath,
        $env:BTSWITCHER_SIGNING_PFX_PASSWORD,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        if (-not $certificate.HasPrivateKey) { throw 'The PFX has no signing private key.' }
        if ((Get-Date) -lt $certificate.NotBefore -or (Get-Date) -gt $certificate.NotAfter) {
            throw 'The signing certificate is not currently valid.'
        }
        if (-not $AllowSelfSignedForTesting -and $certificate.Subject -eq $certificate.Issuer) {
            throw 'A self-signed certificate is for local testing only. Use a trusted production signing certificate.'
        }
        $codeSigningOid = '1.3.6.1.5.5.7.3.3'
        $eku = @($certificate.Extensions | Where-Object {
            $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
        })
        if ($eku.Count -ne 1 -or -not @($eku[0].EnhancedKeyUsages | Where-Object Value -eq $codeSigningOid).Count) {
            throw 'The certificate must have the Code Signing enhanced key usage.'
        }
        $Publisher = $certificate.Subject
        if (-not $PublisherDisplayName) {
            $PublisherDisplayName = $certificate.GetNameInfo(
                [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
        }
        $thumbprint = $certificate.Thumbprint
    }
    finally { $certificate.Dispose() }
}

$dotnet = Get-ProjectDotnet
& $dotnet restore (Join-Path $RepoRoot 'src/BtSwitcher.Extension/BtSwitcher.Extension.csproj') -v:minimal
Assert-NativeSuccess 'Extension dependency restore'
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$sdkTools = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools/10.0.26100.4948/bin/10.0.26100.0/x64'
$makeappx = Join-Path $sdkTools 'makeappx.exe'
$signtool = Join-Path $sdkTools 'signtool.exe'
if (-not (Test-Path -LiteralPath $makeappx) -or
    (-not $PackageOnly -and -not (Test-Path -LiteralPath $signtool))) {
    throw 'Windows SDK packaging tools are missing after extension dependency restore.'
}

$releaseRoot = Join-Path $RepoRoot "artifacts/release/$Version"
if ($PackageOnly) { $releaseRoot = Join-Path $releaseRoot 'unsigned' }
$suffix = if ($PackageOnly) { '_unsigned' } else { '' }
foreach ($architecture in 'x64', 'arm64') {
    $existingPackage = Join-Path $releaseRoot "BtSwitcher_${Version}_${architecture}${suffix}.msix"
    if (Test-Path -LiteralPath $existingPackage) {
        throw "Release output already exists: $existingPackage. Use a new version or clear the old output manually."
    }
}
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
$workRoot = Join-Path $releaseRoot ("work-" + [guid]::NewGuid().ToString('N'))
$existingCertificate = @()
$importedCertificates = @()
$importedCertificate = $null

Push-Location $RepoRoot
try {
    if (-not $PackageOnly) {
        $existingCertificate = @(Get-ChildItem Cert:\CurrentUser\My | ForEach-Object Thumbprint)
        # Select by thumbprint after import: no PFX password is passed to SignTool.
        $importedCertificates = @(Import-PfxCertificate -FilePath $certificatePath `
            -CertStoreLocation Cert:\CurrentUser\My -Password $password -ErrorAction Stop)
        $importedCertificate = $importedCertificates |
            Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
        if (-not $importedCertificate) { throw 'The signing certificate could not be imported.' }
    }

    & $dotnet build src/BtSwitcher.Cli -c Release -v:minimal
    Assert-NativeSuccess 'CLI build'
    & $dotnet test tests/BtSwitcher.Tests -c Release -v:minimal
    Assert-NativeSuccess 'Core tests'

    $packages = @()
    foreach ($architecture in 'x64', 'arm64') {
        $platform = if ($architecture -eq 'x64') { 'x64' } else { 'ARM64' }
        $architectureRoot = Join-Path $workRoot $architecture
        $layout = Join-Path $architectureRoot 'layout'
        New-Item -ItemType Directory -Force -Path $layout | Out-Null

        & $dotnet publish src/BtSwitcher.Extension -c Release -r "win-$architecture" `
            "-p:Platform=$platform" --self-contained true -o $layout -v:minimal
        Assert-NativeSuccess "$architecture extension publish"

        $generated = Join-Path $RepoRoot "src/BtSwitcher.Extension/bin/$platform/Release/net10.0-windows10.0.26100.0/win-$architecture/AppxManifest.xml"
        $manifestPath = Join-Path $layout 'AppxManifest.xml'
        Copy-Item -LiteralPath $generated -Destination $manifestPath -Force
        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        $identity = $manifest.Package.Identity
        if ($identity.ProcessorArchitecture -ne $architecture) {
            throw "Generated manifest architecture does not match $architecture."
        }
        $identity.SetAttribute('Publisher', $Publisher)
        $identity.SetAttribute('Version', $Version)
        $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
        $manifest.Save($manifestPath)

        New-Item -ItemType Directory -Force -Path (Join-Path $layout 'Assets'), `
            (Join-Path $layout 'Public'), (Join-Path $layout 'licenses') | Out-Null
        Copy-Item -Path 'src/BtSwitcher.Extension/Assets/*.png' -Destination (Join-Path $layout 'Assets') -Force
        Copy-Item -LiteralPath LICENSE, NOTICE -Destination $layout -Force
        Copy-Item -LiteralPath 'licenses/PowerToys-MIT.txt' -Destination (Join-Path $layout 'licenses') -Force
        Set-Content -LiteralPath (Join-Path $layout 'Public/README.txt') `
            -Value 'Bluetooth Switcher Command Palette extension.' -Encoding utf8
        if ($architecture -eq 'x64') {
            & "$PSScriptRoot/smoke.ps1" -AppDirectory $layout | Out-Host
        }
        Get-ChildItem -LiteralPath $layout -Recurse -File -Filter '*.pdb' | Remove-Item -Force

        $stagingPackage = Join-Path $architectureRoot 'staging.msix'
        $package = Join-Path $releaseRoot "BtSwitcher_${Version}_${architecture}${suffix}.msix"
        try {
            & $makeappx pack /d $layout /p $stagingPackage /o *> (Join-Path $architectureRoot 'package.log')
            Assert-NativeSuccess "$architecture MSIX packaging"
            if (-not $PackageOnly) {
                & $signtool sign /fd SHA256 /sha1 $thumbprint /tr $TimestampUrl /td SHA256 `
                    $stagingPackage *> (Join-Path $architectureRoot 'sign.log')
                Assert-NativeSuccess "$architecture MSIX signing"
                & $signtool verify /pa /v $stagingPackage *> (Join-Path $architectureRoot 'verify.log')
                Assert-NativeSuccess "$architecture MSIX signature verification"
            }
            Move-Item -LiteralPath $stagingPackage -Destination $package
        }
        catch {
            if (Test-Path -LiteralPath $stagingPackage) { Remove-Item -LiteralPath $stagingPackage -Force }
            throw
        }
        if ($PackageOnly) { Write-Host "Unsigned signing input: $package" }
        else { Write-Host "Signed and verified $package" }
        $packages += $package
    }

    if (-not $PackageOnly) {
        & "$PSScriptRoot/verify-release.ps1" -Version $Version -Publisher $Publisher -Directory $releaseRoot
    }
    if ($PackageOnly) { Write-Host "Unsigned signing inputs (not for distribution): $releaseRoot" }
    else { Write-Host "Verified release assets: $releaseRoot" }
}
finally {
    Pop-Location
    foreach ($imported in $importedCertificates) {
        if ($imported.Thumbprint -notin $existingCertificate) {
            Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($imported.Thumbprint)" -ErrorAction Stop
        }
    }
}
