# Build the Burn bootstrapper Setup.exe (runtime check + on-demand download + MSI).
# Requires: WiX 6, Util + BootstrapperApplications extension dlls, built MSIs.
#
#   .\build-bundle.ps1 -Arch x64 -Edition Perpetual -Version 1.0.8
#   .\build-bundle.ps1 -Arch x64 -Edition Subscription -Version 0.1.0
#   .\build-bundle.ps1 -Arch arm64 -Edition Perpetual -Version 1.0.8
#
# The remote .NET Desktop Runtime payload must be pinned: Hash is the SHA512 of
# the official installer exe (burn verifies SHA512, not SHA1). Bump the table
# below when upgrading the runtime version.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Arch,
  # No default. Both editions carry a <Version> in Directory.Build.props and
  # this script picks the one matching -Edition; a literal default here would
  # silently name a file the caller never built (design doc section 18.5 item 2).
  [Parameter(Mandatory)][ValidateSet('Perpetual','Subscription')][string]$Edition,
  [Parameter(Mandatory)][string]$Version,
  [switch]$SelfContained,
  [string]$CertificateTrustMsiPath,
  [string]$WixExtensionDirectory,
  [string]$DistDir = (Join-Path $PSScriptRoot '..\..\artifacts\dist')
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $Version" }

# Read the version back out of the props file by edition, exactly. The first
# <Version> in the document is Perpetual's, and building Subscription as 1.0.8
# raises nothing. The comparison is a string comparison because PowerShell's XML
# adapter unwraps an attribute-less child element to a string; see the same
# note in build-installer.ps1.
$propsPath = Join-Path $PSScriptRoot '..\..\Directory.Build.props'
# -Encoding UTF8 is mandatory: the file carries Chinese display names, and
# PowerShell 5.1 decodes it as ANSI/GBK otherwise.
[xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
$propsVersion = $props.Project.PropertyGroup |
  Where-Object { $_.WeChatBridgeEdition -eq $Edition } |
  ForEach-Object { $_.Version } |
  Where-Object { $_ } |
  Select-Object -First 1
if (-not $propsVersion) { throw "Directory.Build.props defines no <Version> for edition '$Edition'." }
if ($Version -ne $propsVersion) {
  throw "Version $Version does not match windows\Directory.Build.props for edition '$Edition' ($propsVersion)."
}
$EditionToken = [string](@($props.Project.PropertyGroup |
  Where-Object { $_.WeChatBridgeEdition -eq $Edition } |
  ForEach-Object { $_.EditionToken } |
  Where-Object { $_ } |
  Select-Object -First 1))
if (-not $EditionToken) { throw "Directory.Build.props defines no <EditionToken> for edition '$Edition'." }

# Programs and Features shows the MSI row and the Burn row separately. If both
# read "微信流", a user with the two editions installed on two machines — or
# support reading a bug report — has no way to tell which is which. The MSI's
# own Package/@Name already carries the edition; the bundle has to as well.
$ProductName = [string](@($props.Project.PropertyGroup |
  Where-Object { $_.WeChatBridgeEdition -eq $Edition } |
  ForEach-Object { $_.MsiPackageName } |
  Where-Object { $_ } |
  Select-Object -First 1))
if (-not $ProductName) { throw "Directory.Build.props defines no <MsiPackageName> for edition '$Edition'." }

if (-not $WixExtensionDirectory) {
  throw '-WixExtensionDirectory is required: point it at the directory holding wixtoolset.util.wixext and wixtoolset.ui.wixext.'
}
if (-not (Test-Path -LiteralPath $WixExtensionDirectory)) {
  throw "Wix extension directory does not exist: $WixExtensionDirectory"
}

$extDir = $WixExtensionDirectory
$exts = @(
  "$extDir\wixtoolset.util.wixext\wixext6\WixToolset.Util.wixext.dll",
  "$extDir\wixtoolset.bal.wixext\wixext6\WixToolset.BootstrapperApplications.wixext.dll",
  "$extDir\wixtoolset.netfx.wixext\wixext6\WixToolset.Netfx.wixext.dll"
)

# Official .NET Desktop Runtime installers, pinned per arch.
# Hash = SHA512 of the exe (Get-FileHash -Algorithm SHA512).
$Runtime = @{
  x64 = @{
    Name = 'windowsdesktop-runtime-10.0.12-win-x64.exe'
    Url  = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe'
    Hash = '0B907E9312867172A4EB82F4B5AB3F7C2D25E27D8349546D77EEB5D5B8CBECAB9EBEBFED189B13E9669DC578D892756C548366DA539D47B3DDAC5EFCB7AE72FE'
    Size = '60032984'
    Ver  = '10.0.12.50000'
  }
  arm64 = @{
    Name = 'windowsdesktop-runtime-10.0.12-win-arm64.exe'
    Url  = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-arm64.exe'
    Hash = '909FFED452C1B45572D558B5DBF088927A57854DCD3844B46701EAD6D4D175FB3F329084FDF9B077AA7CDECAECECCACB95C82870D32D7B8C411638331491C441'
    Size = '54031048'
    Ver  = '10.0.12.50000'
  }
}[$Arch]

if ($Arch -eq 'x64') {
  $msi = Join-Path $DistDir "WeChatBridge-$EditionToken-$Version-Windows.msi"
  $out = Join-Path $DistDir "WeChatBridge-$EditionToken-$Version-Windows-Setup.exe"
} else {
  $msi = Join-Path $DistDir "WeChatBridge-$EditionToken-$Version-Windows-arm64.msi"
  $out = Join-Path $DistDir "WeChatBridge-$EditionToken-$Version-Windows-arm64-Setup.exe"
}
$icon = Join-Path $DistDir "payload\$Arch\Assets\AppIcon.ico"
if (-not $CertificateTrustMsiPath) {
  $CertificateTrustMsiPath = Join-Path $DistDir "WeChatBridge-$EditionToken-$Version-CertificateTrust.msi"
}

foreach ($f in @($msi, $icon, $CertificateTrustMsiPath) + $exts) {
  if (-not (Test-Path $f)) { throw "Missing: $f" }
}

wix build (Join-Path $PSScriptRoot 'Bundle.wxs') `
  -ext $exts[0] -ext $exts[1] -ext $exts[2] `
  -arch $Arch `
  -d "SelfContained=$(if ($SelfContained) { 'yes' } else { 'no' })" `
  -d "RuntimePlatform=$Arch" `
  -d "BundleVersion=$Version.0" `
  -d "ProductName=$ProductName" `
  -d "IconFile=$icon" `
  -d "LogoFile=$(Join-Path $DistDir "payload\$Arch\Assets\Square150x150Logo.png")" `
  -d "ThemeFile=$(Join-Path $PSScriptRoot 'InstallerTheme.xml')" `
  -d "LocalizationFile=$(Join-Path $PSScriptRoot 'Installer.zh-CN.wxl')" `
  -d "MsiPath=$msi" `
  -d "CertificateTrustMsiPath=$CertificateTrustMsiPath" `
  -d "RuntimeName=$($Runtime.Name)" `
  -d "RuntimeUrl=$($Runtime.Url)" `
  -d "RuntimeHash=$($Runtime.Hash)" `
  -d "RuntimeSize=$($Runtime.Size)" `
  -d "RuntimeVersion=$($Runtime.Ver)" `
  -o $out

if ($LASTEXITCODE -ne 0) { throw "WiX bundle build failed: $LASTEXITCODE" }
Write-Host "Built $out ($EditionToken $Version)"
