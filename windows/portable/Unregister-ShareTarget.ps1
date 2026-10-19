<#
.SYNOPSIS
  Remove the 微信流 share-target registration created by the portable build.

.DESCRIPTION
  Undoes Register-ShareTarget.ps1: stops the helper, removes the per-user sparse
  package, and drops the record of which drive it was registered from, so a
  later run does not mistake the stick for a moved one.

  Removing the package is enough to unregister it — the external content is
  never copied anywhere, it is referenced in place from the drive. The machine
  level certificates the registration installed are deliberately left alone:
  they are shared with any other copy of 微信流 on this machine, and a user
  cannot remove them without administrator rights in any case.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$installRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$marker = Join-Path $installRoot 'Data\Logs\Install\ShareRegistration.json'

Get-Process -Name 'WeChatBridge.ShareTarget', 'WeChatBridge.Windows' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-AppxPackage -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in 'WeChatBridge.Windows.ShareTarget', 'ChatBridge.Windows.ShareTarget' } |
    Remove-AppxPackage -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
Write-Output 'Share target registration removed.'
