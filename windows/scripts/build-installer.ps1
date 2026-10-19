[CmdletBinding()]
param(
    # Optional cross-check, not the source of truth: leave it out and the value
    # is read from Directory.Build.props below. Passing it is only useful to
    # assert that a caller and the props file still agree.
    [string]$Version,
    # Which of the two products to build. It selects the <Version>, the artifact
    # name token, the MSI package name and the MSIX capability set together, so
    # they cannot be mixed: the version lives in the same conditioned
    # PropertyGroup as the token, and this match is exact.
    [ValidateSet('Perpetual', 'Subscription')]
    [string]$Edition = 'Perpetual',
    [string]$DistDirectory = (Join-Path $PSScriptRoot '..\artifacts\dist'),
    # No machine-specific default: this used to point at another checkout's
    # scratch directory, which fails with an opaque path error on anyone else.
    # Required — validated below alongside the signing thumbprint.
    [string]$WixExtensionDirectory,
    # No machine-specific thumbprint default: new-dev-certificate.ps1 mints a fresh
    # certificate per machine, so a hardcoded one goes stale silently. Resolved from
    # the subject below, the same way register-dev.ps1 does.
    [string]$SigningThumbprint,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric components.' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$DistDirectory = [IO.Path]::GetFullPath($DistDirectory)
# The product version lives in Directory.Build.props; five call sites used to
# hardcode it independently and drifted silently. Assert instead.
#
# The edition matters here, and not as a nicety: both editions have a <Version>,
# so taking the first one found, which is what this used to do, returns
# Perpetual's 1.0.8 every single time and raises nothing. A Subscription build
# would ship under the buy-out's version number with a Subscription payload.
# Match the PropertyGroup whose WeChatBridgeEdition attribute is this build's
# edition, exactly, and fail loudly when there is no such group.
$propsPath = Join-Path $repositoryRoot 'windows\Directory.Build.props'
# -Encoding UTF8 is mandatory, not decorative: the file carries Chinese display
# names, and PowerShell 5.1 would otherwise decode it as ANSI/GBK.
[xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
# PowerShell's XML adapter unwraps an attribute-less child element to a plain
# string but hands back an XmlElement when the element carries attributes, so
# this comparison is a string comparison only because the conditioned groups
# declare <WeChatBridgeEdition> with no Condition attribute. Add one and the
# filter silently stops matching anything.
$editionGroups = @($props.Project.PropertyGroup | Where-Object { $_.WeChatBridgeEdition -eq $Edition })
if (-not $editionGroups) { throw "Directory.Build.props has no PropertyGroup conditioned on WeChatBridgeEdition='$Edition'." }
$productVersion = $editionGroups | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $productVersion) { throw "Directory.Build.props defines no <Version> for edition '$Edition'." }
# The token and the MSI name ride along with the version for the same reason:
# one PropertyGroup is the only place any of them can be edited, so a release
# cannot bump one and forget the others.
$EditionToken = [string]($editionGroups | ForEach-Object { $_.EditionToken } | Where-Object { $_ } | Select-Object -First 1)
$MsiPackageName = [string]($editionGroups | ForEach-Object { $_.MsiPackageName } | Where-Object { $_ } | Select-Object -First 1)
if (-not $EditionToken) { throw "Directory.Build.props defines no <EditionToken> for edition '$Edition'." }
if (-not $MsiPackageName) { throw "Directory.Build.props defines no <MsiPackageName> for edition '$Edition'." }
if (-not $Version) {
    $Version = [string]$productVersion
}
elseif ($Version -ne $productVersion) {
    throw "Version $Version does not match windows\Directory.Build.props for edition '$Edition' ($productVersion). Bump the props file instead of passing -Version."
}
# Minimum-version guard, scoped to Perpetual. Its reason is historical and
# edition-specific: the existing Windows application files already carry file
# version 1.0.0, so an MSI below 1.0.1 makes Windows Installer skip the
# application components on upgrade. Subscription has no install history at
# all, so there is nothing to skip, and 0.1.0 is the whole point of its version
# line.
#
# Consequence of the shared UpgradeCode (design doc 18.3): 0.1.0 is LOWER than
# an installed Perpetual 1.0.8, so MajorUpgrade reports WIX_DOWNGRADE_DETECTED
# and the Subscription installer refuses to run over it. Moving between the two
# editions is uninstall-then-install. That is a real constraint of the two
# decisions combined, not an oversight here, and it has not been reconciled in
# the design doc.
if ($Edition -eq 'Perpetual' -and [version]$Version -lt [version]'1.0.1') {
    throw 'Version must be at least 1.0.1: existing Windows application files already use version 1.0.0.'
}
# x64 is the only architecture built today. arm64 needs a RID here, a payload
# directory, -arch on both wix invocations, and build-bundle.ps1's runtime table.
$payload = Join-Path $DistDirectory 'payload\x64'
$msiDirectory = Join-Path $repositoryRoot 'windows\packaging\msi'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
if (-not $WixExtensionDirectory) {
    throw '-WixExtensionDirectory is required: point it at the directory holding wixtoolset.util.wixext and wixtoolset.ui.wixext.'
}
if (-not (Test-Path -LiteralPath $WixExtensionDirectory)) {
    throw "Wix extension directory does not exist: $WixExtensionDirectory"
}
if (-not $SigningThumbprint) {
    $SigningThumbprint = (Get-ChildItem Cert:\CurrentUser\My |
        Where-Object Subject -eq 'CN=WeChatBridge Windows Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1).Thumbprint
    if (-not $SigningThumbprint) { throw 'No development certificate found. Run new-dev-certificate.ps1 or pass -SigningThumbprint.' }
}
$certificate = Get-Item "Cert:\CurrentUser\My\$SigningThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'A valid signing certificate with a private key is required.' }
[xml]$helperManifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'windows\share-target.manifest') -Raw -Encoding UTF8
$helperManifest.assembly.msix.publisher = $certificate.Subject
$helperManifestPath = Join-Path $DistDirectory 'share-target-build.manifest'
$helperManifest.Save($helperManifestPath)
if (-not $SkipPublish) {
    & dotnet publish (Join-Path $repositoryRoot 'windows\src\WeChatBridge.Windows\WeChatBridge.Windows.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" "-p:WeChatBridgeEdition=$Edition" -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Main publish failed.' }
    & dotnet publish (Join-Path $repositoryRoot 'windows\src\WeChatBridge.ShareTarget\WeChatBridge.ShareTarget.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" "-p:WeChatBridgeEdition=$Edition" "-p:ApplicationManifest=$helperManifestPath" -o (Join-Path $payload 'share-target') --nologo
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
& (Join-Path $PSScriptRoot 'pack-msix.ps1') -Publisher $certificate.Subject -Edition $Edition -Version "$Version.0" -OutputPath $msix -ExternalContentDirectory $payload
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
# The edition token goes into every file name: two products that ship artifacts
# called WeChatBridge-1.0.8-* and WeChatBridge-0.1.0-* are distinguishable, but a
# support request quoting "the installer" is not, and a Subscription MSI that
# looks like a Perpetual one is the failure this whole split exists to prevent.
$trustMsi = Join-Path $DistDirectory "WeChatBridge-$EditionToken-$Version-CertificateTrust.msi"
& wix build (Join-Path $msiDirectory 'CertificateTrust.wxs') -ext $util -arch x64 -d "MsiVersion=$Version.0" -d "PayloadDir=$payload" -o $trustMsi
if ($LASTEXITCODE -ne 0) { throw 'Certificate trust prerequisite build failed.' }
& signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $trustMsi
if ($LASTEXITCODE -ne 0) { throw 'Certificate trust prerequisite signing failed.' }
$msi = Join-Path $DistDirectory "WeChatBridge-$EditionToken-$Version-Windows.msi"
# MsiPackageName feeds Package/@Name (Programs and Features), EditionToken the
# edition registry value and the artifact file names. UpgradeCode deliberately
# stays shared: the two editions are mutually exclusive on one machine, so the
# second install supersedes the first and the Share Target registration path
# stays byte-identical (design doc section 18.3).
& wix build (Join-Path $msiDirectory 'Package.wxs') -ext $util -ext $ui -arch x64 -d "MsiVersion=$Version.0" -d "PayloadDir=$payload" -d "EditionToken=$EditionToken" -d "MsiPackageName=$MsiPackageName" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
& signtool sign /fd SHA256 /sha1 $SigningThumbprint /s My $msi
if ($LASTEXITCODE -ne 0) { throw 'MSI signing failed.' }
& (Join-Path $msiDirectory 'build-bundle.ps1') -Arch x64 -Edition $Edition -Version $Version -DistDir $DistDirectory -SelfContained -CertificateTrustMsiPath $trustMsi -WixExtensionDirectory $WixExtensionDirectory
$setup = Join-Path $DistDirectory "WeChatBridge-$EditionToken-$Version-Windows-Setup.exe"
# Burn needs both its cached engine and the full self-contained bundle signed.
$signingDirectory = Join-Path $DistDirectory 'signing'
New-Item -ItemType Directory -Path $signingDirectory -Force | Out-Null
$engine = Join-Path $signingDirectory "WeChatBridge-$EditionToken-$Version-engine.exe"
$reattached = Join-Path $signingDirectory "WeChatBridge-$EditionToken-$Version-signed-bundle.exe"
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
Write-Output "Edition: $Edition ($EditionToken)"
Write-Output "Offline installer: $setup"
