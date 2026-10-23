[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$package = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'WeChatBridge.Windows.ShareTarget', 'ChatBridge.Windows.ShareTarget' }
if ($package) {
    $package | Remove-AppxPackage
    Write-Output "Unregistered: $($package.PackageFullName)"
} else {
    Write-Output 'Package is not registered.'
}
