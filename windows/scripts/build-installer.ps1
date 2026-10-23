[CmdletBinding()]
param(
    [string]$Version = '1.0.8',
    [string]$DistDirectory = (Join-Path $PSScriptRoot '..\artifacts\dist'),
    [string]$WixExtensionDirectory = 'D:\WeChatB-Hub\_scratch\tools\wixext',
    [string]$SigningThumbprint = '40ED85A6287A88A950D0FEE9EA3C9DBC032358CD',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric components.' }
if ([version]$Version -lt [version]'1.0.1') { throw 'Version must be at least 1.0.1: existing Windows application files already use version 1.0.0.' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$DistDirectory = [IO.Path]::GetFullPath($DistDirectory)
$payload = Join-Path $DistDirectory 'payload\x64'
$msiDirectory = Join-Path $repositoryRoot 'windows\packaging\msi'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$certificate = Get-Item "Cert:\CurrentUser\My\$SigningThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'A valid signing certificate with a private key is required.' }
[xml]$helperManifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'windows\share-target.manifest') -Raw -Encoding UTF8
$helperManifest.assembly.msix.publisher = $certificate.Subject
$helperManifestPath = Join-Path $DistDirectory 'share-target-build.manifest'
$helperManifest.Save($helperManifestPath)
if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repositoryRoot 'windows\src\WeChatBridge.Windows\WeChatBridge.Windows.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Main publish failed.' }
    & dotnet publish (Join-Path $repositoryRoot 'windows\src\WeChatBridge.ShareTarget\WeChatBridge.ShareTarget.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" "-p:ApplicationManifest=$helperManifestPath" -o (Join-Path $payload 'share-target') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'ShareTarget publish failed.' }
}
# A self-contained payload must not silently become a framework-dependent installer.
foreach ($applicationFile in @(
    'WeChatBridge.Windows.exe', 'WeChatBridge.Windows.dll', 'WeChatBridge.Windows.Core.dll',
    'share-target\WeChatBridge.ShareTarget.exe', 'share-target\WeChatBridge.ShareTarget.dll',
    'share-target\WeChatBridge.Windows.Core.dll'
)) {
    $file = Get-Item -LiteralPath (Join-Path $payload $applicationFile)
    if ([version]$file.VersionInfo.FileVersion -ne [version]"$Version.0") {
        throw "Application file version does not match installer version: $applicationFile ($($file.VersionInfo.FileVersion))"
    }
}
foreach ($hostDirectory in @($payload, (Join-Path $payload 'share-target'))) {
    foreach ($runtimeFile in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','PresentationFramework.dll')) {
        if (-not (Test-Path (Join-Path $hostDirectory $runtimeFile))) { throw "Missing embedded runtime: $runtimeFile" }
    }
}
$certDirectory = Join-Path $payload 'certs'
New-Item -ItemType Directory -Path $certDirectory -Force | Out-Null
Export-Certificate -Cert $certificate -FilePath (Join-Path $certDirectory 'WeChatBridge.Windows.Dev.cer') -Force | Out-Null
$issuer = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $certificate.Issuer | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $issuer) { throw 'The public issuing certificate was not found.' }
Export-Certificate -Cert $issuer -FilePath (Join-Path $certDirectory 'WeChatBridge.Windows.Dev.Root.cer') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $msiDirectory 'Register-ShareTarget.ps1'), (Join-Path $msiDirectory 'Unregister-ShareTarget.ps1') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt') -Force
$license = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'LICENSE')).Replace('\','\\').Replace('{','\{').Replace('}','\}').Replace("`r",'').Replace("`n",'\par ')
[IO.File]::WriteAllText((Join-Path $payload 'LICENSE.rtf'), ('{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs20 '+$license+'}'))
$assets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Get-ChildItem (Join-Path $repositoryRoot 'windows\packaging\SparsePackage\Assets') -File | Copy-Item -Destination $assets -Force
$msix = Join-Path $payload 'WeChatBridge.ShareTarget.msix'
& (Join-Path $PSScriptRoot 'pack-msix.ps1') -Publisher $certificate.Subject -Version "$Version.0" -OutputPath $msix -ExternalContentDirectory $payload
& (Join-Path $PSScriptRoot 'Test-SharePackage.ps1') -InstallRoot $payload
foreach ($file in @(
    (Join-Path $payload 'WeChatBridge.Windows.exe'),
    (Join-Path $payload 'share-target\WeChatBridge.ShareTarget.exe'),
    $msix
)) {
    & signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $file" }
}
$util = Join-Path $WixExtensionDirectory 'wixtoolset.util.wixext\wixext6\WixToolset.Util.wixext.dll'
$ui = Join-Path $WixExtensionDirectory 'wixtoolset.ui.wixext\wixext6\WixToolset.UI.wixext.dll'
$trustMsi = Join-Path $DistDirectory "WeChatBridge-$Version-CertificateTrust.msi"
& wix build (Join-Path $msiDirectory 'CertificateTrust.wxs') -ext $util -arch x64 -d "MsiVersion=$Version.0" -d "PayloadDir=$payload" -o $trustMsi
if ($LASTEXITCODE -ne 0) { throw 'Certificate trust prerequisite build failed.' }
& signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $trustMsi
if ($LASTEXITCODE -ne 0) { throw 'Certificate trust prerequisite signing failed.' }
$msi = Join-Path $DistDirectory "WeChatBridge-$Version-Windows.msi"
& wix build (Join-Path $msiDirectory 'Package.wxs') -ext $util -ext $ui -arch x64 -d "MsiVersion=$Version.0" -d "PayloadDir=$payload" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
& signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $msi
if ($LASTEXITCODE -ne 0) { throw 'MSI signing failed.' }
& (Join-Path $msiDirectory 'build-bundle.ps1') -Arch x64 -Version $Version -DistDir $DistDirectory -SelfContained -CertificateTrustMsiPath $trustMsi -WixExtensionDirectory $WixExtensionDirectory
$setup = Join-Path $DistDirectory "WeChatBridge-$Version-Windows-Setup.exe"
# Burn needs both its cached engine and the full self-contained bundle signed.
$signingDirectory = Join-Path $DistDirectory 'signing'
New-Item -ItemType Directory -Path $signingDirectory -Force | Out-Null
$engine = Join-Path $signingDirectory "WeChatBridge-$Version-engine.exe"
$reattached = Join-Path $signingDirectory "WeChatBridge-$Version-signed-bundle.exe"
& wix burn detach $setup -engine $engine
if ($LASTEXITCODE -ne 0) { throw 'Burn engine extraction failed.' }
& signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $engine
if ($LASTEXITCODE -ne 0) { throw 'Burn engine signing failed.' }
& wix burn reattach $setup -engine $engine -o $reattached
if ($LASTEXITCODE -ne 0) { throw 'Signed Burn engine reattachment failed.' }
Copy-Item -LiteralPath $reattached -Destination $setup -Force
& signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $setup
if ($LASTEXITCODE -ne 0) { throw 'Setup signing failed.' }
foreach ($file in @($msix, $trustMsi, $msi, $engine, $setup)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid') { throw "Signature validation failed: $file ($($signature.Status))" }
}
if (Get-ChildItem $payload -Recurse -File | Where-Object Extension -in '.pfx','.p12','.key','.pem') { throw 'Private signing material must never be packaged.' }
$hashes = foreach ($file in @($trustMsi, $msi, $setup)) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($file) }
$hashes | Set-Content -LiteralPath (Join-Path $DistDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Offline installer: $setup"
