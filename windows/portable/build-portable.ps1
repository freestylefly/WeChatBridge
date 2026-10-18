<#
.SYNOPSIS
  Build the 微信流 portable (USB stick) distribution.

.DESCRIPTION
  Produces a self-contained payload, a signed sparse MSIX for package identity,
  and a self-extracting ZIP laid out the way FirstRun.bat expects:

      <zip root>\
          FirstRun.bat
          FirstRun.ps1
          使用说明.txt
          微信流\                      <- PortableRoot
              WeChatBridge.Windows.exe / .dll
              share-target\...
              Assets\  Resources\  resources.pri
              WeChatBridge.ShareTarget.msix
              certs\*.cer
              Register-ShareTarget.ps1  Unregister-ShareTarget.ps1
              portable.flag
              Vault\31.微信会话\  Vault\.obsidian\
              Data\                 <- created at run time

  PortableRoot is the directory containing share-target\, NOT the ZIP root.
  Both -InstallRoot here and pack-msix.ps1's -ExternalContentDirectory must be
  that directory; pointing either at the ZIP root registers successfully and
  then silently does nothing, because the manifest's AllowExternalContent
  resolves relative to it.

  Reuses pack-msix.ps1, Test-SharePackage.ps1 and new-dev-certificate.ps1 rather
  than reimplementing them. Requirements are theirs: .NET 10 SDK; makepri,
  makeappx, signtool and mt.exe on PATH or in a Windows Kits bin directory.
  mt.exe is not part of the Microsoft.Windows.SDK.BuildTools NuGet package, so
  Test-SharePackage.ps1 needs a real Windows SDK for the mt step.

.PARAMETER Version
  Optional cross-check only; the value comes from
  windows/Directory.Build.props for the Perpetual edition. Passing it asserts
  that the caller and the props file still agree.

.PARAMETER SigningThumbprint
  Certificate in Cert:\CurrentUser\My. Defaults to the newest
  "CN=WeChatBridge Windows Dev" certificate. Pass a real code-signing
  certificate's thumbprint for anything you distribute.

.PARAMETER OutputDirectory
  Defaults to windows/artifacts/portable.

.PARAMETER SkipPublish
  Reuse an existing payload — for iterating on the layout without republishing.

.PARAMETER SkipZip
  Leave the staged directory in place without packing the ZIP.

.EXAMPLE
  ./windows/portable/build-portable.ps1
  ./windows/portable/build-portable.ps1 -SigningThumbprint ABC123... -OutputDirectory D:\stick
#>
# NOTE: there is deliberately no -Edition parameter here, and $Edition below is
# hardcoded rather than exposed. The portable build IS the free tier of the
# buy-out product (design doc §7.1), not a build of Subscription: it is a
# zero-network build, its licence travels with the stick while the account does
# not, and a Subscription payload on a USB stick would ship an MSIX that
# declares internetClient next to binaries and a vault written to removable
# media. build-installer.ps1 and build-bundle.ps1 both take -Edition; please do
# not add one here "for symmetry" — make it an explicit, deliberate change.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\portable'),
    [string]$SigningThumbprint,
    [switch]$SkipPublish,
    [switch]$SkipZip
)
$ErrorActionPreference = 'Stop'

$Edition = 'Perpetual'
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric components.' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# The portable root IS the directory holding the executable: AppLayout resolves
# it as AppContext.BaseDirectory and derives Data\ from it. Introducing an extra
# app\ level here would break that and silently strand the data one level up.
$payload = Join-Path $OutputDirectory '微信流'
$msix = Join-Path $payload 'WeChatBridge.ShareTarget.msix'
$AppLayoutMarker = 'portable.flag'

# Same lookup as build-installer.ps1: five call sites used to carry this value
# independently and drifted without anyone noticing. The edition is matched
# exactly; both editions have a <Version> now, and taking the first one found
# would mean Perpetual every time without complaining. The comparison is a
# string comparison because PowerShell's XML adapter unwraps an attribute-less
# child element to a string.
# -Encoding UTF8 is mandatory: Directory.Build.props carries Chinese display
# names and PowerShell 5.1 decodes it as ANSI/GBK otherwise.
[xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'windows\Directory.Build.props') -Raw -Encoding UTF8
$editionGroups = @($props.Project.PropertyGroup | Where-Object { $_.WeChatBridgeEdition -eq $Edition })
$productVersion = $editionGroups | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $productVersion) { throw "Directory.Build.props defines no <Version> for edition '$Edition'." }
$EditionToken = [string]($editionGroups | ForEach-Object { $_.EditionToken } | Where-Object { $_ } | Select-Object -First 1)
if (-not $EditionToken) { throw "Directory.Build.props defines no <EditionToken> for edition '$Edition'." }
if (-not $Version) {
    $Version = [string]$productVersion
}
elseif ($Version -ne $productVersion) {
    throw "Version $Version does not match windows\Directory.Build.props for edition '$Edition' ($productVersion)."
}

if (-not $SigningThumbprint) {
    $SigningThumbprint = (Get-ChildItem Cert:\CurrentUser\My |
        Where-Object Subject -eq 'CN=WeChatBridge Windows Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1).Thumbprint
    if (-not $SigningThumbprint) { throw 'No development certificate found. Run new-dev-certificate.ps1 or pass -SigningThumbprint.' }
}
$certificate = Get-Item "Cert:\CurrentUser\My\$SigningThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'A valid signing certificate with a private key is required.' }

if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

# The helper's embedded manifest has to name the certificate that actually signs
# the package, or Test-SharePackage.ps1's mt.exe comparison rejects it.
[xml]$helperManifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'windows\share-target.manifest') -Raw -Encoding UTF8
$helperManifest.assembly.msix.publisher = $certificate.Subject
# Build-time intermediate, not shipped: it must not end up in SHA256SUMS.txt.
$helperManifestPath = Join-Path $env:TEMP "WeChatBridge-share-target-$Version.manifest"
$helperManifest.Save($helperManifestPath)

if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repositoryRoot 'windows\src\WeChatBridge.Windows\WeChatBridge.Windows.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" "-p:WeChatBridgeEdition=$Edition" -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Main publish failed.' }
    & dotnet publish (Join-Path $repositoryRoot 'windows\src\WeChatBridge.ShareTarget\WeChatBridge.ShareTarget.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" "-p:WeChatBridgeEdition=$Edition" "-p:ApplicationManifest=$helperManifestPath" -o (Join-Path $payload 'share-target') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'ShareTarget publish failed.' }
}

# A self-contained payload must not quietly become a framework-dependent stick.
foreach ($applicationFile in @(
    'WeChatBridge.Windows.exe', 'WeChatBridge.Windows.dll', 'WeChatBridge.Windows.Core.dll',
    'share-target\WeChatBridge.ShareTarget.exe', 'share-target\WeChatBridge.ShareTarget.dll',
    'share-target\WeChatBridge.Windows.Core.dll'
)) {
    $file = Get-Item -LiteralPath (Join-Path $payload $applicationFile)
    if ([version]$file.VersionInfo.FileVersion -ne [version]"$Version.0") {
        throw "Application file version does not match the build version: $applicationFile ($($file.VersionInfo.FileVersion))"
    }
}
foreach ($hostDirectory in @($payload, (Join-Path $payload 'share-target'))) {
    foreach ($runtimeFile in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path (Join-Path $hostDirectory $runtimeFile))) { throw "Missing embedded runtime: $runtimeFile" }
    }
}

# pack-msix.ps1 copies resources.pri and nothing else. Without the visual assets
# the manifest's Logo/Square44x44Logo/Square150x150Logo dangle and the shell
# silently declines to enumerate the share target — registration succeeds, the
# menu row never appears.
$assets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Get-ChildItem (Join-Path $repositoryRoot 'windows\packaging\SparsePackage\Assets') -File | Copy-Item -Destination $assets -Force

$certDirectory = Join-Path $payload 'certs'
New-Item -ItemType Directory -Path $certDirectory -Force | Out-Null
# These two names are hardcoded by Register-ShareTarget.ps1.
Export-Certificate -Cert $certificate -FilePath (Join-Path $certDirectory 'WeChatBridge.Windows.Dev.cer') -Force | Out-Null
$issuer = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $certificate.Issuer | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $issuer) { throw 'The public issuing certificate was not found.' }
Export-Certificate -Cert $issuer -FilePath (Join-Path $certDirectory 'WeChatBridge.Windows.Dev.Root.cer') -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Register-ShareTarget.ps1'), (Join-Path $PSScriptRoot 'Unregister-ShareTarget.ps1') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt') -Force

& (Join-Path $repositoryRoot 'windows\scripts\pack-msix.ps1') -Publisher $certificate.Subject -Edition $Edition -Version "$Version.0" -OutputPath $msix -ExternalContentDirectory $payload
& (Join-Path $repositoryRoot 'windows\scripts\Test-SharePackage.ps1') -InstallRoot $payload

foreach ($file in @(
    (Join-Path $payload 'WeChatBridge.Windows.exe'),
    (Join-Path $payload 'share-target\WeChatBridge.ShareTarget.exe'),
    $msix
)) {
    & signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $file" }
}
foreach ($file in @($msix)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid') { throw "Signature validation failed: $file ($($signature.Status))" }
}

# The marker is what makes this build portable at run time.
New-Item -ItemType File -Path (Join-Path $payload $AppLayoutMarker) -Force | Out-Null

$vault = Join-Path $payload 'Vault'
New-Item -ItemType Directory -Path (Join-Path $vault '31.微信会话') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $vault '.obsidian') -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FirstRun.ps1'), (Join-Path $PSScriptRoot 'FirstRun.bat') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.txt') -Destination $OutputDirectory -Force

if (Get-ChildItem $OutputDirectory -Recurse -File | Where-Object Extension -in '.pfx', '.p12', '.key', '.pem') {
    throw 'Private signing material must never be packaged.'
}

$hashes = Get-ChildItem $OutputDirectory -Recurse -File |
    ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.FullName.Substring($OutputDirectory.Length + 1) }
$hashes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii

Write-Output "Edition: $Edition ($EditionToken)"
Write-Output "Portable payload: $payload"
if ($SkipZip) { exit 0 }

$zip = Join-Path ([IO.Path]::GetDirectoryName($OutputDirectory)) "WeChatBridge-$EditionToken-$Version-Windows-Portable.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
# Stage just what ships, so the ZIP has no build-only leftovers.
$staging = Join-Path $env:TEMP "WeChatBridge-portable-$EditionToken-$Version"
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -LiteralPath $payload -Destination (Join-Path $staging '微信流') -Recurse
foreach ($name in 'FirstRun.ps1', 'FirstRun.bat', '使用说明.txt', 'LICENSE.txt', 'SHA256SUMS.txt') {
    $source = Join-Path $OutputDirectory $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $staging -Force }
}
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Output "Portable ZIP: $zip"
