[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallRoot)
$installRoot = New-Item -ItemType Directory -Force -Path $installRoot | Select-Object -ExpandProperty FullName
$registrationLog = Join-Path $installRoot 'registration.log'
function Write-RegistrationLog([string]$message) {
    Add-Content -Path $registrationLog -Value ("{0:O} {1}" -f [DateTimeOffset]::UtcNow, $message)
}
trap {
    $hresult = ('0x{0:X8}' -f ($_.Exception.HResult -band 0xffffffff))
    Write-RegistrationLog "失败 $hresult $($_.Exception.Message)"
    throw
}

$helper = Join-Path $installRoot 'share-target\WeChatBridge.ShareTarget.exe'
$manifestDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\packaging\SparsePackage'))
$manifestPath = Join-Path $manifestDir 'AppxManifest.xml'
$packagePath = Join-Path $installRoot 'WeChatBridge.ShareTarget.msix'

if (-not (Test-Path $helper)) { throw "Share Target helper not found: $helper" }
if (-not (Test-Path (Join-Path $installRoot 'WeChatBridge.Windows.exe'))) { throw 'WPF host not found under InstallRoot.' }
if (-not (Test-Path $manifestPath)) { throw "Sparse package manifest not found: $manifestPath" }

# An AllowExternalContent package resolves the paths its manifest declares against the
# external location, not against the payload copy under WindowsApps. The logos therefore
# have to exist under the install root as well, or the shell cannot resolve the share
# target and silently leaves it out of the share sheet.
$assetSource = Join-Path $manifestDir 'Assets'
if (Test-Path $assetSource) {
    $assetTarget = Join-Path $installRoot 'Assets'
    New-Item -ItemType Directory -Force -Path $assetTarget | Out-Null
    Get-ChildItem -Path $assetSource -Force | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination (Join-Path $assetTarget $_.Name) -Recurse -Force
    }
    Write-RegistrationLog "已同步图标资源到外部内容目录：$assetTarget"
}

if (-not $CertificateThumbprint) {
    $CertificateThumbprint = (Get-ChildItem Cert:\CurrentUser\My |
        Where-Object Subject -eq 'CN=WeChatBridge Windows Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1).Thumbprint
}
if (-not $CertificateThumbprint) { throw 'Run new-dev-certificate.ps1 first.' }
$certificate = Get-ChildItem Cert:\CurrentUser\My\$CertificateThumbprint -ErrorAction SilentlyContinue
if (-not $certificate) { throw "Development certificate not found: $CertificateThumbprint" }

$packScript = Join-Path $PSScriptRoot 'pack-msix.ps1'
& $packScript -Publisher 'CN=WeChatBridge Windows Dev' -OutputPath $packagePath -ExternalContentDirectory $installRoot
$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signTool) {
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $signTool) { throw 'signtool.exe was not found. Install the Windows SDK or add it to PATH.' }
$signToolPath = if ($signTool.FullName) { $signTool.FullName } else { $signTool.Source }
& $signToolPath sign /fd SHA256 /sha1 $CertificateThumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE." }

$signature = Get-AuthenticodeSignature -FilePath $packagePath
if ($signature.Status -ne 'Valid') {
    throw "The MSIX signature is not trusted ($($signature.Status): $($signature.StatusMessage)). Trust the development certificate in Cert:\CurrentUser\Root and retry."
}

Get-AppxPackage -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in 'WeChatBridge.Windows.ShareTarget', 'ChatBridge.Windows.ShareTarget' } |
    Remove-AppxPackage -ErrorAction SilentlyContinue
try {
    Add-AppxPackage -Path $packagePath -ExternalLocation $installRoot
}
catch {
    $hresult = ('0x{0:X8}' -f ($_.Exception.HResult -band 0xffffffff))
    throw "Add-AppxPackage failed ($hresult): $($_.Exception.Message)"
}
$registered = Get-AppxPackage -Name 'WeChatBridge.Windows.ShareTarget' -ErrorAction SilentlyContinue
if (-not $registered) { throw 'Add-AppxPackage returned but the package is not registered.' }
Write-RegistrationLog "成功 $($registered.PackageFullName)"
Write-Output "Registered: $($registered.PackageFullName)"
