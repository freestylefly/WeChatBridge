# Build the Burn bootstrapper Setup.exe (runtime check + on-demand download + MSI).
# Requires: WiX 6, Util + BootstrapperApplications extension dlls, built MSIs.
#
#   .\build-bundle.ps1 -Arch x64
#   .\build-bundle.ps1 -Arch arm64
#
# The remote .NET Desktop Runtime payload must be pinned: Hash is the SHA512 of
# the official installer exe (burn verifies SHA512, not SHA1). Bump the table
# below when upgrading the runtime version.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Arch,
  [string]$Version = '1.0.8',
  [switch]$SelfContained,
  [string]$CertificateTrustMsiPath,
  [string]$WixExtensionDirectory = 'D:\WeChatB-Hub\_scratch\tools\wixext',
  [string]$DistDir = (Join-Path $PSScriptRoot '..\..\artifacts\dist')
)

$ErrorActionPreference = 'Stop'

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
  $msi = Join-Path $DistDir "WeChatBridge-$Version-Windows.msi"
  $out = Join-Path $DistDir "WeChatBridge-$Version-Windows-Setup.exe"
} else {
  $msi = Join-Path $DistDir "WeChatBridge-$Version-Windows-arm64.msi"
  $out = Join-Path $DistDir "WeChatBridge-$Version-Windows-arm64-Setup.exe"
}
$icon = Join-Path $DistDir "payload\$Arch\Assets\AppIcon.ico"
if (-not $CertificateTrustMsiPath) {
  $CertificateTrustMsiPath = Join-Path $DistDir "WeChatBridge-$Version-CertificateTrust.msi"
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
Write-Host "Built $out"
